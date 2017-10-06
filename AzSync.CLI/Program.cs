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

            if (args.Contains("-v") || args.Contains("--verbose"))
            {
                LConfig = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss}<{ThreadId:d2}> [{Level:u3}] [{SourceContext}] {Message}{NewLine}{Exception}");
            }
            else
            {
                LConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
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
                if (errors.Any(e => e.Tag == ErrorType.VersionRequestedError))
                {
                    L.Info("AzSync, version {0}.", Version.ToString(4));
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.HelpVerbRequestedError))
                {
                    HelpVerbRequestedError error = (HelpVerbRequestedError)errors.First(e => e.Tag == ErrorType.HelpVerbRequestedError);
                    HelpText help = GetAutoBuiltHelpText(result);
                    help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                    help.Copyright = string.Empty;
                    help.AddPreOptionsLine(string.Empty);
                    if (error.Type != null)
                    {
                        help.AddVerbs(error.Type);
                    }
                    L.Info(help);
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.HelpRequestedError))
                {
                    HelpRequestedError error = (HelpRequestedError)errors.First(e => e.Tag == ErrorType.HelpRequestedError);
                    HelpText help = GetAutoBuiltHelpText(result);
                    help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                    help.Copyright = string.Empty;
                    L.Info(help);
                    Exit(ExitResult.SUCCESS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.NoVerbSelectedError))
                {
                    NoVerbSelectedError error = (NoVerbSelectedError)errors.First(e => e.Tag == ErrorType.NoVerbSelectedError);
                    HelpText help = GetAutoBuiltHelpText(result);
                    help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                    help.Copyright = string.Empty;
                    help.AddVerbs(typeof(SyncOptions), typeof(CopyOptions), typeof(GenerateOptions));
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.UnknownOptionError))
                {
                    UnknownOptionError error = (UnknownOptionError)errors.First(e => e.Tag == ErrorType.UnknownOptionError);
                    HelpText help = GetAutoBuiltHelpText(result);
                    help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                    help.Copyright = string.Empty;
                    L.Error("Unknown option: {error}.", error.Tag);
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else
                {
                    HelpText help = GetAutoBuiltHelpText(result);
                    help.Copyright = string.Empty;
                    L.Error("An error occurred parsing the program options: {errors}.", errors);
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
                        }
                        else
                        {
                            L.Error("Could not find local directory {0}.", o.Source);
                            Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                        }
                    }
                    catch (IOException ioe)
                    {
                        L.Error(ioe, "A storage error occurred  attempting to find local directory {d}.", o.Source);
                        Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                    }
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
                EngineOptions.Add("OperationType", TransferEngine.OperationType.COPY);
                if (string.IsNullOrEmpty(o.Source) || string.IsNullOrEmpty(o.Destination))
                {
                    L.Error("You must specify both the source and destination parameters for an upload operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                if (EngineOptions.ContainsKey("SourceUri") && EngineOptions.ContainsKey("DestinationUri"))
                {
                    L.Error("You must specify a local file or directory path and an Azure Storage location as the source/destination for an upload operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }

                if (!EngineOptions.ContainsKey("SourceUri") && EngineOptions.ContainsKey("SourceFiles"))
                {
                    if (!EngineOptions.ContainsKey("DestinationUri") || !EngineOptions.ContainsKey("DestinationAccountName") || !EngineOptions.ContainsKey("DestinationContainerName"))
                    {
                        L.Error("The destination for a local filesystem copy operation must be an Azure Storage Blob Service endpoint Url.");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }
                    if (!EngineOptions.ContainsKey("DestinationKey"))
                    {
                        L.Error("You must specify the account key for accessing the destination Azure Storage location.");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }

                }
                else if (!EngineOptions.ContainsKey("DestinationUri") && EngineOptions.ContainsKey("DestinationDirectory"))
                {
                    if (!EngineOptions.ContainsKey("SourceUri") || !EngineOptions.ContainsKey("SourceAccountName") || !EngineOptions.ContainsKey("SourceContainerName"))
                    {
                        L.Error("The source for an Azure Storage copy operation must be an Azure Storage Blob Service endpoint Url.");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }
                    if (!EngineOptions.ContainsKey("SourceKey"))
                    {
                        L.Error("You must specify the account key for accessing the source Azure Storage location.");
                        Exit(ExitResult.INVALID_OPTIONS);
                    }
                }
                ExecuteTransferTasks();
            })
            .WithParsed((GenerateOptions o) =>
            {
                GenerateOptions = o;
                ExecuteGenerateTasks();
            })
            .WithParsed((SyncOptions o) =>
            {
                o.Source = AppConfig["Source"] == string.Empty ? o.Source : AppConfig["Source"];
                o.Destination = AppConfig["Destination"] == string.Empty ? o.Destination : AppConfig["Destination"];
                o.SourceKey = AppConfig["SourceKey"] == string.Empty ? o.SourceKey : AppConfig["SourceKey"];
                o.DestinationKey = AppConfig["DestKey"] == string.Empty ? o.DestinationKey : AppConfig["DestKey"];

                if (string.IsNullOrEmpty(o.Source) || string.IsNullOrEmpty(o.Destination))
                {
                    L.Error("You must specify both the source and destination parameters for a sync operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                if (o.Source.ToLower().StartsWith("https://") && string.IsNullOrEmpty(o.SourceKey))
                {
                    L.Error("You must specify the account key for accessing the source Azure Storage object.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                if (o.Destination.ToLower().StartsWith("https://") && string.IsNullOrEmpty(o.DestinationKey))
                {
                    L.Error("You must specify the account key for accessing the destination Azure Storage object.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                ExecuteTransferTasks();
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
                using (Operation engineOp = L.Begin("Azure Storage {op}", EngineOptions["OperationType"]))
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
