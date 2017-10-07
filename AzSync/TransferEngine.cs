using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.DataMovement;

using Octodiff.Core;

using Serilog;
using SerilogTimings;


namespace AzSync
{
    public class TransferEngine : ILogging
    {
        #region Enums
        public enum OperationType
        {
            COPY,
            SYNC,
            INFO
        }

        public enum TransferDirection
        {
            UP,
            DOWN
        }
        #endregion

        #region Constructors
        public TransferEngine(Dictionary<string, object> engineOptions, CancellationToken ct, IConfigurationRoot configuration, TextWriter output)
        {
            Contract.Requires(engineOptions.ContainsKey("OperationType"));
            Contract.Requires(engineOptions.ContainsKey("Source") || engineOptions.ContainsKey("Destination"));
            EngineOptions = engineOptions;
            AppConfig = configuration;
            CT = ct;

            ServicePointManager.DefaultConnectionLimit = Environment.ProcessorCount * 8;
            ServicePointManager.Expect100Continue = false;

            foreach (PropertyInfo prop in this.GetType().GetProperties())
            {
                if (EngineOptions.ContainsKey(prop.Name) && prop.CanWrite)
                {
                    prop.SetValue(this, EngineOptions[prop.Name]);
                }
            }

            TransferManager.Configurations.BlockSize = this.BlockSizeKB * 1024;

            if (this.Operation == OperationType.COPY)
            {
                Contract.Requires(!string.IsNullOrEmpty(Source) && !string.IsNullOrEmpty(Destination));
                Contract.Requires(!(EngineOptions.ContainsKey("SourceUri") && EngineOptions.ContainsKey("DestinationUri")));
                if (this.EngineOptions.ContainsKey("DestinationUri"))
                {
                    Contract.Requires(EngineOptions.ContainsKey("DestinationKey") && EngineOptions.ContainsKey("DestinationAccountName") && EngineOptions.ContainsKey("DestinationContainerName"));
                    Contract.Requires(EngineOptions.ContainsKey("SourceFiles"));
                    string cs = UseStorageEmulator ? "UseDevelopmentStorage=true" : AzStorage.GetConnectionString(DestinationUri, DestinationKey);
                    if (string.IsNullOrEmpty(cs))
                    {
                        L.Error("Could not determine the Azure Storage connection string.");
                        return;
                    }
                    else
                    {
                        DestinationStorage = new AzStorage(this, cs, true);
                        if (!DestinationStorage.Initialised)
                        {
                            L.Error("Could not initialise destination Azure storage.");
                            return;
                        }
                        else
                        {
                            this.Initialised = true;
                            Direction = TransferDirection.UP;
                        }
                    }
                }
                else
                {
                    Contract.Requires(EngineOptions.ContainsKey("SourceKey") && EngineOptions.ContainsKey("SourceAccountName") && EngineOptions.ContainsKey("SourceContainerName"));
                    Contract.Requires(EngineOptions.ContainsKey("DestinationDirectory"));
                    string cs = UseStorageEmulator ? "UseDevelopmentStorage=true" : AzStorage.GetConnectionString(SourceUri, SourceKey);
                    if (string.IsNullOrEmpty(cs))
                    {
                        L.Error("Could not determine the Azure Storage connection string.");
                        return;
                    }
                    else
                    {
                        SourceStorage = new AzStorage(this, cs, true);
                        if (!SourceStorage.Initialised)
                        {
                            L.Error("Could not intialise source Azure storage.");
                            Initialised = false;
                            return;
                        }
                        else
                        {
                            Initialised = true;
                            Direction = TransferDirection.DOWN;
                        }
                    }
                }
            }
        }
        #endregion

        #region Methods
        public async Task<bool> Transfer()
        {
            switch (this.Operation)
            {
                case OperationType.COPY:
                    if (Direction == TransferDirection.UP)
                    {
                        return await Upload();
                    }
                    else
                    {
                        return await Download();
                    }
                case OperationType.SYNC:
                    if (Direction == TransferDirection.UP)
                    {
                        return await SyncLocalToRemote();
                    }
                    else
                    {
                        return await SyncRemoteToLocal();
                    }
                default:
                    throw new NotImplementedException();
            }
        }

        protected async Task<bool> Upload()
        {
            if (SourceFiles.Length == 1)
            {
                bool u = await UploadSingleFile(SourceFiles[0]);
                return u;
            }
            return await Task.FromResult(true);
        }

        protected async Task<bool> UploadSingleFile(string fileName)
        {
            FileInfo file = new FileInfo(fileName);
            if (string.IsNullOrEmpty(JournalFilePath))
            {
                JournalFilePath = file.FullName + ".azsj";
            }
            OpenTransferJournal();
            UploadOptions options = new UploadOptions();
            SingleTransferContext context = new SingleTransferContext(JournalStream);
            context.LogLevel = LogLevel.Informational;
            context.ProgressHandler = new SingleFileUploadProgressReporter(file);
            context.SetAttributesCallback = (destination) =>
            {
                DestinationBlob = destination as CloudBlob;
                DestinationBlob.Properties.ContentType = this.ContentType;
            };
            context.ShouldOverwriteCallback = (source, destination) =>
            {
                bool o = this.Overwrite;
                return o;
            };
            context.FileTransferred += Context_FileTransferred;
            context.FileFailed += Context_FileFailed;
            context.FileSkipped += Context_FileSkipped;
            try
            {
                DestinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, file.Name, BlobType.BlockBlob);
                FileSignature sig = new FileSignature(file);
                SignatureTask = Task.Factory.StartNew(() => sig.Compute(), CT, TaskCreationOptions.None, TaskScheduler.Default);
                TransferTask = TransferManager.UploadAsync(file.FullName, DestinationBlob, options, context, CT);
                Task.WaitAll(TransferTask, SignatureTask);
                SignatureBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, DestinationBlob.Name + ".sig", BlobType.BlockBlob) as CloudBlockBlob;
                CloudBlobStream signatureStream = await SignatureBlob.OpenWriteAsync();
            }
            catch (TaskCanceledException)
            {
                L.Info("The upload operation was cancelled.");
                return true;
            }
            catch (OperationCanceledException)
            {
                L.Info("The upload operation was cancelled.");
                return true;
            }
            catch (StorageException se)
            {
                L.Error(se, $"A storage error occurred attempting to upload file {file.Name} to a cloud block blob in container {DestinationContainerName}");
                return false;
            }
            catch (TransferException te)
            {
                if (CT.IsCancellationRequested)
                {
                    L.Info("The upload operation was cancelled by the user.");
                    return true;
                }
                else
                {
                    L.Error(te, $"A transfer error occurred attempting to upload file {file.Name} to a cloud block blob in container {DestinationContainerName}");
                    return false;
                }
            }
            finally
            {
                CloseTransferJournal();
            }

            return true;
        }

        protected async Task<bool> UploadMultipleFiles()
        {
            /*
           UploadDirectoryOptions options = new UploadDirectoryOptions
           {
               SearchPattern = Pattern,
               Recursive = Recurse,
               BlobType = BlobType.BlockBlob
           };
           DirectoryTransferContext ctx = new DirectoryTransferContext();
           ctx.LogLevel = LogLevel.Verbose;*/
            return true;
        }

        protected async Task<bool> Download()
        {
            return await Task.FromResult(true);
        }

        protected async Task<bool> SyncLocalToRemote()
        {
            Contract.Requires(SourceFiles.Length > 0);
            Contract.Requires(DestinationStorage != null);

            if (SourceFiles.Length == 1)
            {
                return await SyncLocalSingleFileToRemote(SourceFiles[0]);
            }
            else return false;
        }


        protected async Task<bool> SyncRemoteToLocal()
        {
            return false;
        }

 
        protected async Task<bool> SyncLocalSingleFileToRemote(string fileName)
        {
            return true;
        }

        protected byte[] ComputeFileSignature(FileInfo file)
        {
            SignatureBuilder builder = new SignatureBuilder();
            using (FileStream basisStream = file.OpenRead())
            using (MemoryStream signatureStream = new MemoryStream())
            {
                builder.Build(basisStream, new OctoSigWriter(signatureStream));
            }
            return null;
        }

        protected bool OpenTransferJournal()
        {
            Contract.Requires(!string.IsNullOrEmpty(JournalFilePath));
            try
            {
                JournalFile = new FileInfo(JournalFilePath);
                if (JournalFile.Exists)
                {
                    if (DeleteJournal)
                    {
                        JournalFile.Delete();
                        L.Info("Deleted existing journal file {file}.", JournalFilePath);
                        JournalFile = new FileInfo(JournalFilePath);
                        JournalStream = JournalFile.Open(FileMode.CreateNew, FileAccess.ReadWrite);
                        L.Info("Created new journal file {file}.", JournalFile.FullName);
                    }
                    else
                    {
                        JournalStream = JournalFile.Open(FileMode.Open, FileAccess.ReadWrite);
                        L.Info("Resuming transfer from journal file {file}.", JournalFile.FullName);
                    }
                }
                else
                {
                    L.Info("Creating journal file {file} for transfer.", JournalFile.FullName);
                    JournalStream = JournalFile.Open(FileMode.CreateNew, FileAccess.ReadWrite);
                }
                return true;
            }
            catch (IOException ioe)
            {
                L.Error(ioe, "An I/O error occurred attempting to use the journal file {file}. No journal file for this transfer will be used,", JournalFile.FullName);
                JournalStream = new MemoryStream();
                return false;
            }
            catch (Exception e)
            {
                L.Error(e, "An I/O error occurred attempting to use the journal file {file}. No journal file for this transfer will be used,", JournalFile.FullName);
                JournalStream = new MemoryStream();
                return false;
            }
        }

        protected void CloseTransferJournal()
        {
            if (JournalStream != null)
            {
                JournalStream.Flush();
                JournalStream.Dispose();
            }
        }

        private void Context_FileFailed(object sender, TransferEventArgs e)
        {
            if (CT.IsCancellationRequested)
            {
                L.Warn("Transfer of file {file} was cancelled before completion by the user.", e.Source);
            }
            else
            {
                L.Warn("Transfer of file {file} failed.", e.Source);
            }
            
        }

        private void Context_FileSkipped(object sender, TransferEventArgs e)
        {
            
        }

        private void Context_FileTransferred(object sender, TransferEventArgs e)
        {
            if (Operation == OperationType.COPY && this.Direction == TransferDirection.UP && SourceFiles.Length == 1)
            {
                DestinationBlob = (CloudBlockBlob)e.Destination;
            }
            L.Info("Transfer of file {file} completed in {millisec} seconds.", e.Source, (e.EndTime - e.StartTime).TotalMilliseconds);
        }

        public static string PrintBytes(double bytes)
        {
            if (bytes >= 0 && bytes <= 1024)
            {
                return string.Format("{0:N0} B/s", bytes);
            }
            else if (bytes >= 1024 && bytes < (1024 * 1024))
            {
                return string.Format("{0:N2} KB/s", bytes / 1024);
            }
            else if (bytes >= (1024 * 1024) && bytes < (1024 * 1024 * 1024))
            {
                return string.Format("{0:N2} MB/s", bytes / (1024 * 10124));
            }
            else if (bytes >= (1024 * 1024 * 1024))
            {
                return string.Format("{0:N2} GB/s", bytes / (1024 * 1024 * 1024));
            }
            else throw new ArgumentOutOfRangeException();
            
        }
        #endregion

        #region Properties
        public bool Initialised { get; protected set; } = false;
        public IConfigurationRoot AppConfig { get; protected set; }
        public Dictionary<string, object> EngineOptions { get; protected set; }
        public OperationType Operation { get; protected set; }
        public TransferDirection Direction { get; protected set; }
        public string Source { get; protected set; }
        public Uri SourceUri { get; protected set; }
        public string SourceKey { get; protected set; }
        public string SourceAccountName { get; protected set; }
        public string SourceContainerName { get; protected set; }
        public string[] SourceFiles { get; protected set; }
        public DirectoryInfo SourceDirectory { get; protected set; }
        public AzStorage SourceStorage { get; protected set; }
        public CloudBlob SourceBlob { get; protected set; }
        public string Destination { get; protected set; }
        public Uri DestinationUri { get; protected set; }
        public string DestinationKey { get; protected set; }
        public string DestinationAccountName { get; protected set; }
        public string DestinationContainerName { get; protected set; }
        public DirectoryInfo DestinationDirectory { get; protected set; }
        public AzStorage DestinationStorage { get; protected set; }
        public CloudBlob DestinationBlob { get; protected set; }
        public string ContentType { get; protected set; }
        public int BlockSizeKB { get; protected set; }
        public int RetryCount { get; protected set; }
        public int RetryTime { get; protected set; }
        public bool Overwrite { get; protected set; }
        public string Pattern { get; protected set; }
        public bool Recurse { get; protected set; }
        public bool UseStorageEmulator { get; protected set; }
        public string JournalFilePath { get; protected set; }
        public FileInfo JournalFile { get; protected set; }
        public bool NoJournal { get; protected set; }
        public bool DeleteJournal { get; protected set; }
        public Stream JournalStream { get; protected set; }
        public Task TransferTask { get; protected set; }
        public Task SignatureTask { get; protected set; }
        public CloudBlockBlob SignatureBlob { get; protected set; }
        #endregion

        #region Fields
        protected Logger<TransferEngine> L = new Logger<TransferEngine>();
        protected CancellationToken CT;
        #endregion
    }
}
