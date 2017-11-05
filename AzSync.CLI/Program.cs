using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Enrichers;
using SerilogTimings;
using CommandLine;
using CommandLine.Text;

namespace AzSync.CLI
{
    class Program : ILogging
    {
        public enum ExitResult
        {
            SUCCESS = 0,
            UNHANDLED_EXCEPTION = 1,
            INVALID_OPTIONS = 2,
            FILE_OR_DIRECTORY_NOT_FOUND = 3,
            TRANSFER_ENGINE_INIT_ERROR = 4,
            TRANSFER_ERROR = 5,
            GENERATE_ERROR = 6
        }

        static Version Version = Assembly.GetExecutingAssembly().GetName().Version;
        static IConfigurationRoot AppConfig;
        static LoggerConfiguration LConfig;
        static Logger<Program> L;
        static TransferEngine Engine;
        static Dictionary<string, object> EngineOptions = new Dictionary<string, object>(3);
        static GenerateOptions GenerateOptions;
        static CancellationTokenSource CTS = new CancellationTokenSource();
        static Task<ExitResult> TransferTask;
        static Task<ExitResult> GenerateTask;
        static Task ReadConsoleKeyTask;

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += Program_UnhandledException;

            Console.CancelKeyPress += Console_CancelKeyPress;

            LConfig = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId();
            
            if (args.Contains("-v") || args.Contains("--verbose"))
            {
                LConfig = LConfig.MinimumLevel.Verbose()
                    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss}<{ThreadId:d2}> [{Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}");
            }
            else
            {
                LConfig = LConfig
                    .MinimumLevel.Debug()
                    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss}<{ThreadId:d2}> [{Level:u3}] {Message}{NewLine}{Exception}");
            }

            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();
            AppConfig = builder.Build();

            Log.Logger = LConfig.CreateLogger();
            L = new Logger<Program>();

            ParserResult<object> result = new Parser().ParseArguments<Options, GenerateOptions, CopyOptions, SyncOptions>(args);
            result.WithNotParsed((IEnumerable<Error> errors) =>
            {
                HelpText help = GetAutoBuiltHelpText(result);
                help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                help.Copyright = string.Empty;
                help.AddPreOptionsLine(string.Empty);

                if (errors.Any(e => e.Tag == ErrorType.VersionRequestedError))
                {
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.HelpVerbRequestedError))
                {
                    HelpVerbRequestedError error = (HelpVerbRequestedError)errors.First(e => e.Tag == ErrorType.HelpVerbRequestedError);
                    if (error.Type != null)
                    {
                        help.AddVerbs(error.Type);
                    }
                    L.Info(help);
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.HelpRequestedError))
                {
                    help.AddVerbs(typeof(SyncOptions), typeof(CopyOptions), typeof(GenerateOptions));
                    L.Info(help);
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.NoVerbSelectedError))
                {
                    help.AddVerbs(typeof(SyncOptions), typeof(CopyOptions), typeof(GenerateOptions));
                    L.Error("No operation selected. Specify one of: copy, sync, gen.");
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.MissingRequiredOptionError))
                {
                    MissingRequiredOptionError error = (MissingRequiredOptionError)errors.First(e => e.Tag == ErrorType.MissingRequiredOptionError);
                    L.Error("A required option is missing: {0}.", error.NameInfo.NameText);
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.UnknownOptionError))
                {
                    UnknownOptionError error = (UnknownOptionError)errors.First(e => e.Tag == ErrorType.UnknownOptionError);
                    help.AddVerbs(typeof(SyncOptions), typeof(CopyOptions), typeof(GenerateOptions));
                    L.Error("Unknown option: {error}.", error.Token);
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else
                {
                    L.Error("An error occurred parsing the program options: {errors}.", errors);
                    help.AddVerbs(typeof(SyncOptions), typeof(CopyOptions), typeof(GenerateOptions));
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
            })
            .WithParsed((TransferOptions o) =>
            {
                o.Source = string.IsNullOrEmpty(AppConfig["Source"]) ? o.Source : AppConfig["Source"];
                o.SourceKey = string.IsNullOrEmpty(AppConfig["SourceKey"]) ? o.SourceKey : AppConfig["SourceKey"];
                o.Destination = string.IsNullOrEmpty(AppConfig["Destination"]) ? o.Destination : AppConfig["Destination"];
                o.DestinationKey = string.IsNullOrEmpty(AppConfig["DestKey"]) ? o.DestinationKey : AppConfig["DestKey"];
                o.Pattern = string.IsNullOrEmpty(AppConfig["Pattern"]) ? o.Pattern : AppConfig["Pattern"];

                if (o.UseStorageEmulator)
                {
                    o.DestinationKey = @"Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
                }

                foreach (PropertyInfo prop in o.GetType().GetProperties())
                {
                    EngineOptions.Add(prop.Name, prop.GetValue(o));
                }

                if (!string.IsNullOrEmpty(o.Source) && (o.Source.StartsWith("http://") || o.Source.StartsWith("https://")) && Uri.TryCreate(o.Source, UriKind.Absolute, out Uri sourceUri))
                {
                    if (sourceUri.Segments.Length < 3)
                    {
                        L.Error("The Azure endpoint Url for the sync source must be in the format http(s)://{account_name}.blob.core.windows.net/{container_name}");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }
                    else
                    {
                        EngineOptions.Add("SourceUri", sourceUri);
                        EngineOptions.Add("SourceAccountName", sourceUri.Segments[1].TrimEnd('/'));
                        EngineOptions.Add("SourceContainerName", sourceUri.Segments[2]);
                        if (sourceUri.Segments.Length == 4)
                        {
                            EngineOptions.Add("SourceBlobName", sourceUri.Segments[3]);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(o.Source))
                {
                    try
                    {
                        if (Directory.Exists(o.Source))
                        {
                            EngineOptions.Add("SourceDirectory", new DirectoryInfo(o.Source));
                            string[] files = Directory.GetFiles(o.Source, o.Pattern, o.Recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                            L.Info("Matched {0} file(s) to pattern {1} in directory {2}.", files.Length, o.Pattern, o.Source);
                            if (files.Length > 0)
                            {
                                EngineOptions.Add("SourceFiles", files);
                            }
                            else
                            {
                                L.Warn("Nothing to do, exiting.");
                                Exit(ExitResult.SUCCESS);
                            }
                        }
                        else
                        {
                            L.Error("Could not find local directory {0}.", o.Source);
                            Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                        }
                    }
                    catch (IOException ioe)
                    {
                        L.Error(ioe, "A storage error occurred attempting to find or access local directory {d}.", o.Source);
                        Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                    }

                }

                if (!string.IsNullOrEmpty(o.Destination) && (o.Destination.StartsWith("http://") || o.Destination.StartsWith("https://")) && Uri.TryCreate(o.Destination, UriKind.Absolute, out Uri destinationUri))
                {
                    if (destinationUri.Segments.Length != 3)
                    {
                        L.Error("The Azure endpoint Url for the sync destination must be in the format http(s)://{account_name}.blob.core.windows.net/{container_name}");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }
                    else
                    {
                        EngineOptions.Add("DestinationUri", destinationUri);
                        EngineOptions.Add("DestinationAccountName", destinationUri.Segments[1].TrimEnd('/'));
                        EngineOptions.Add("DestinationContainerName", destinationUri.Segments[2]);
                        if (destinationUri.Segments.Length == 4)
                        {
                            EngineOptions.Add("DestinationBlobName", destinationUri.Segments[3]);
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(o.Destination))
                {
                    try
                    {
                        if (Directory.Exists(o.Destination))
                        {
                            EngineOptions.Add("DestinationDirectory", new DirectoryInfo(o.Destination));
                        }
                        else
                        {
                            L.Error("Could not find local directory {0}.", o.Destination);
                            Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                        }
                    }
                    catch (IOException ioe)
                    {
                        L.Error(ioe, "A storage exception was thrown attempting to find local directory {d}.", o.Destination);
                        Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                    }
                }
            })
            .WithParsed((CopyOptions o) =>
            {
                EngineOptions.Add("Operation", TransferEngine.OperationType.COPY);
                ExecuteTransferTasks();
            })
            .WithParsed((SyncOptions o) =>
            {
                EngineOptions.Add("Operation", TransferEngine.OperationType.SYNC);
                ExecuteTransferTasks();
            })
            .WithParsed((GenerateOptions o) =>
            {
                GenerateOptions = o;
                ExecuteGenerateTasks();
            });
        }

        static async Task<ExitResult> Transfer()
        {
            using (Operation programOp = L.Begin("Azure Storage transfer operation"))
            {
                using (Operation engineOp = L.Begin("Initialising transfer engine"))
                {
                    Engine = new TransferEngine(EngineOptions, CTS.Token, AppConfig, Console.Out);
                    if (!Engine.Initialised)
                    {
                        return ExitResult.TRANSFER_ENGINE_INIT_ERROR;
                    }
                    else
                    {
                        engineOp.Complete();
                    }
                }
                using (Operation engineOp = L.Begin("Azure Storage {op}", EngineOptions["Operation"]))
                {
                    if (await Engine.Transfer())
                    {
                        engineOp.Complete();
                        programOp.Complete();
                        return ExitResult.SUCCESS;
                    }
                    else
                    {
                        return ExitResult.TRANSFER_ERROR;
                    }
                }
            }
        }

        static void ExecuteTransferTasks()
        {
            Task[] tasks = { TransferTask = Transfer(), ReadConsoleKeyTask = Task.Run(() => ReadConsoleKeys()) };
            try
            {
                int c = Task.WaitAny(tasks, CTS.Token);
                if (c == 0)
                {
                    Exit(TransferTask.Result);
                }
                else
                {
                    TransferTask.Wait();
                    Exit(ExitResult.SUCCESS);
                }
            }
            catch (AggregateException ae)
            {
                foreach (Exception e in ae.InnerExceptions)
                {
                    if (e is TaskCanceledException)
                    {
                        L.Info("Sync operation cancelled by user.");
                        Exit(ExitResult.SUCCESS);
                    }
                    else
                    {
                        L.Error(e, "An error occurred during the transfer operation.");
                        Exit(ExitResult.UNHANDLED_EXCEPTION);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                TransferTask.Wait();
                Exit(ExitResult.SUCCESS);
            }
            finally
            {
                CTS.Dispose();
            }


        }

        static async Task<ExitResult> Generate(string fileName, int sizeMB, int averageSizeKB)
        {
            
            if (!File.Exists(fileName))
            {
                await Generator.GenerateFile(CTS.Token, fileName, sizeMB, averageSizeKB);
            }
            else
            {
                await Generator.ModifyFile(CTS.Token, new FileInfo(fileName), sizeMB, averageSizeKB);
            }
            return ExitResult.SUCCESS;
            
        }

        static void ExecuteGenerateTasks()
        {
            Task[] tasks = { GenerateTask = Generate(GenerateOptions.Name, GenerateOptions.SizeMB, GenerateOptions.PartSizeKB), ReadConsoleKeyTask = Task.Run(() => ReadConsoleKeys()) };
            try
            {
                int c = Task.WaitAny(tasks, CTS.Token);
                if (c == 0)
                {
                    Exit(GenerateTask.Result);
                }
                else
                {
                    L.Warn("Write operations to the file {file} were interrupted. The file may no longer be a valid archive and should be deleted and re-generated.");
                    Exit(ExitResult.SUCCESS);
                }
            }
            catch (AggregateException ae)
            {
                foreach (Exception e in ae.InnerExceptions)
                {
                    if (e is TaskCanceledException)
                    {
                        L.Info("Sync operation cancelled by user.");
                        Exit(ExitResult.SUCCESS);
                    }
                    else if (e is IOException)
                    {
                        L.Error(e, "An I/O error occurred during generation of test file.");
                        Exit(ExitResult.GENERATE_ERROR);
                    }
                    else
                    {
                        L.Error(e, "An unknown error occurred during generation of test file.");
                        Exit(ExitResult.GENERATE_ERROR);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Exit(ExitResult.SUCCESS);
            }
            finally
            {
                CTS.Dispose();
            }
        }

        static void ReadConsoleKeys()
        {
            ConsoleKeyInfo cki = Console.ReadKey(true);
            if (((cki.Modifiers & ConsoleModifiers.Control) != 0) && (cki.Key == ConsoleKey.Q))
            {
                L.Info("Ctrl-Q stop requested by user.");
                CTS.Cancel();
                return;
            }
        }

        static void Exit(ExitResult result)
        {
            Log.CloseAndFlush();
            if (CTS != null)
            {
                CTS.Dispose();
            }

            Environment.Exit((int)result);
        }

        static int ExitWithCode(ExitResult result)
        {
            Log.CloseAndFlush();
            return (int)result;
        }

        static HelpText GetAutoBuiltHelpText(ParserResult<object> result)
        {
            return HelpText.AutoBuild(result, h =>
            {
                h.AddOptions(result);
                return h;
            },
            e =>
            {
                return e;
            });
        }

        static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            L.Info("Stop transfer requested by user.");
            CTS.Cancel();
            if (TransferTask != null && !TransferTask.IsCompleted)
            {
                TransferTask.Wait();
            }
            Exit(ExitResult.SUCCESS);
        }

        static void Program_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (CTS != null)
            {
                CTS.Dispose();
            }
            try
            {
                L.Error(e.ExceptionObject as Exception, "An unhandled runtime exception occurred. AzSync CLI will terminate.");
                Log.CloseAndFlush();
            }
            catch (Exception exc)
            {
                Console.WriteLine("An unhandled runtime exception occurred. Additionally an exception was thrown logging this event: {0}\n{1}\n AzSync CLI will terminate.", exc.Message, exc.StackTrace);
            }
            if (e.IsTerminating)
            {
                Environment.Exit((int)ExitResult.UNHANDLED_EXCEPTION);
            }
        }

        

    }
}
