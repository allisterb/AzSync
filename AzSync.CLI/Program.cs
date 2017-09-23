using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Serilog;
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
            ANALYSIS_ENGINE_INIT_ERROR = 4,
            SYNC_ERROR = 5
        }

        static Version Version = Assembly.GetExecutingAssembly().GetName().Version;
        static IConfigurationRoot AppConfig;
        static LoggerConfiguration LConfig;
        static Logger<Program> L;
        static SyncEngine Engine;
        static Dictionary<string, object> EngineOptions = new Dictionary<string, object>(3);

        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += Program_UnhandledException;

            if (args.Contains("-v") || args.Contains("--verbose"))
            {
                LConfig = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .WriteTo.Console();
            }
            else
            {
                LConfig = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .WriteTo.Console();
            }

            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();
            AppConfig = builder.Build();

            Log.Logger = LConfig.CreateLogger();
            L = new Logger<Program>();

            ParserResult<object> result = new Parser().ParseArguments<Options, UpOptions, SyncOptions>(args);
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
                    help.AddVerbs(typeof(SyncOptions), typeof(UpOptions));
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else if (errors.Any(e => e.Tag == ErrorType.UnknownOptionError))
                {
                    UnknownOptionError error = (UnknownOptionError)errors.First(e => e.Tag == ErrorType.NoVerbSelectedError);
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
                    help.Heading = new HeadingInfo("AzSync", Version.ToString(3));
                    help.Copyright = string.Empty;
                    L.Error("An error occurred parsing the program options: {errors}.", errors);
                    L.Info(help);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
            })
            .WithParsed((Options o) =>
            {
                o.Source = string.IsNullOrEmpty(AppConfig["Source"]) ? o.Source : AppConfig["Source"];
                o.SourceKey = string.IsNullOrEmpty(AppConfig["SourceKey"]) ? o.SourceKey : AppConfig["SourceKey"];
                o.Destination = string.IsNullOrEmpty(AppConfig["Destination"]) ? o.Destination : AppConfig["Destination"];
                o.DestinationKey = string.IsNullOrEmpty(AppConfig["DestKey"]) ? o.DestinationKey : AppConfig["DestKey"];

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
                    if (sourceUri.Segments.Length != 3)
                    {
                        L.Error("The Azure endpoint Url for the sync source must be in the format http(s)://{host}/{account_name}/{container_name}");
                    }
                    else
                    {
                        EngineOptions.Add("SourceAccountName", sourceUri.Segments[1].TrimEnd('/'));
                        EngineOptions.Add("SourceContainerName", sourceUri.Segments[2]);
                        EngineOptions.Add("SourceUri", sourceUri);
                    }
                }
                if (!string.IsNullOrEmpty(o.Destination) && (o.Destination.StartsWith("http://") || o.Destination.StartsWith("https://")) && Uri.TryCreate(o.Destination, UriKind.Absolute, out Uri destinationUri))
                {
                    if (destinationUri.Segments.Length != 3)
                    {
                        L.Error("The Azure endpoint Url for the sync destination must be in the format http(s)://{host}/{account_name}/{container_name}");
                    }
                    else
                    {
                        EngineOptions.Add("DestinationAccountName", destinationUri.Segments[1].TrimEnd('/'));
                        EngineOptions.Add("DestinationContainerName", destinationUri.Segments[2]);
                        EngineOptions.Add("DestinationUri", destinationUri);
                    }
                }
            })
            .WithParsed((UpOptions o) =>
            {
                EngineOptions.Add("OperationType", SyncEngine.OperationType.UPLOAD);
                if (string.IsNullOrEmpty(o.Source) || string.IsNullOrEmpty(o.Destination))
                {
                    L.Error("You must specify both the source and destination parameters for an upload operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                if (o.Source.ToLower().StartsWith("https://") || o.Source.ToLower().StartsWith("http://"))
                {
                    L.Error("You must specify a local file or directory path as the source for an upload operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                if (!EngineOptions.ContainsKey("DestinationUri") || !EngineOptions.ContainsKey("DestinationAccountName") || !EngineOptions.ContainsKey("DestinationContainerName"))
                {
                    L.Error("The destination for an upload operation must be an Azure Storage Blob Service endpoint Url.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                try
                {
                    if (File.Exists(o.Source))
                    {
                        EngineOptions.Add("SourceFile", new FileInfo(o.Source));
                    }
                    else if (Directory.Exists(o.Source))
                    {
                        EngineOptions.Add("SourceDirectory", new DirectoryInfo(o.Source));
                    }
                }
                catch (IOException ioe)
                {
                    L.Warn("I/O exception thrown searching for file or directory {s}: {m}.", o.Source, ioe.Message);
                    Exit(ExitResult.FILE_OR_DIRECTORY_NOT_FOUND);
                }

                if (!o.Destination.ToLower().StartsWith("http://") && !o.Destination.ToLower().StartsWith("https://"))
                {
                    L.Error("You must specify an Azure Storage endpoint as the destination for an upload operation.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                else if (!EngineOptions.ContainsKey("DestinationUri"))
                {
                    L.Error("The destination Azure Storage endpoint {d} is not a valid Uri.", o.Destination);
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                
                if (string.IsNullOrEmpty(o.DestinationKey)) 
                {
                    L.Error("You must specify the account key for accessing the destination Azure Storage container.");
                    Exit(ExitResult.INVALID_OPTIONS);
                }
                Sync().Wait();
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
                Sync().Wait();
            });
        }

        static async Task Sync()
        {
            using (Operation programOp = L.Begin("Azure Storage sync operation"))
            {
                using (Operation engineOp = L.Begin("Initialising sync engine"))
                {
                    Engine = new SyncEngine(EngineOptions, AppConfig, Console.Out);
                    if (!Engine.Initialised)
                    {
                        Exit(ExitResult.ANALYSIS_ENGINE_INIT_ERROR);
                    }
                    else
                    {
                        engineOp.Complete();
                    }
                }
                using (Operation engineOp = L.Begin("Azure Storage {up}", EngineOptions["OperationType"]))
                {
                    if (await Engine.Sync())
                    {
                        engineOp.Complete();
                        programOp.Complete();
                        Exit(ExitResult.SUCCESS);
                    }
                    else
                    {
                        Exit(ExitResult.SYNC_ERROR);
                    }
                }
            }
        }

        static void Exit(ExitResult result)
        {
            Log.CloseAndFlush();
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
        static void Program_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
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
