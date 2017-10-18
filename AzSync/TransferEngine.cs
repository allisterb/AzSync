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

            foreach (PropertyInfo prop in this.GetType().GetProperties())
            {
                if (EngineOptions.ContainsKey(prop.Name) && prop.CanWrite)
                {
                    prop.SetValue(this, EngineOptions[prop.Name]);
                }
            }

            TransferManager.Configurations.BlockSize = this.BlockSizeKB * 1024;
            ServicePointManager.DefaultConnectionLimit = Environment.ProcessorCount * 8;
            ServicePointManager.Expect100Continue = false;

            if (this.Operation == OperationType.COPY || this.Operation == OperationType.SYNC)
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
                        L.Error("Could not determine the destination Azure Storage connection string.");
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
                            Initialised = true;
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
                        L.Error("Could not determine the source Azure Storage connection string.");
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

        protected async Task<bool> UploadSingleFile(string fileName)
        {
            FileInfo file = new FileInfo(fileName);
            L.Info("{file} has size {size}", file.FullName, PrintBytes(file.Length, ""));
            if (string.IsNullOrEmpty(JournalFilePath))
            {
                JournalFilePath = file.FullName + ".azsj";
            }
            OpenTransferJournal();
            UploadOptions options = new UploadOptions();
            SingleTransferContext context = new SingleTransferContext(JournalStream);
            context.LogLevel = LogLevel.Informational;
            context.ProgressHandler = new SingleFileTransferProgressReporter(file);
            context.SetAttributesCallback = (destination) =>
            {
                DestinationBlob = destination as CloudBlob;
                DestinationBlob.Properties.ContentType = this.ContentType;
            };
            context.ShouldOverwriteCallback = (source, destination) =>
            {
                CloudBlob b = (CloudBlob)destination;
                bool o = this.Overwrite;
                if (o)
                {
                    L.Info("Overwriting existing blob {container}/{blob}.", DestinationContainerName, b.Name);
                }
                else
                {
                    L.Warn("Not overwriting existing blob {container}/{blob}.", DestinationContainerName, b.Name);
                }
                return o;
            };
            context.FileTransferred += Context_FileTransferred;
            context.FileFailed += Context_FileFailed;
            context.FileSkipped += Context_FileSkipped;
            try
            {
                DestinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, file.Name, BlobType.BlockBlob);
                using (Operation azOp = L.Begin("Upload file"))
                {
                    Signature = new SingleFileSignature(file, DestinationBlob);
                    TransferTask = TransferManager.UploadAsync(file.FullName, DestinationBlob, options, context, CT);
                    Task.WaitAll(TransferTask);
                    if (TransferTask.Status == TaskStatus.RanToCompletion)
                    {
                        azOp.Complete();
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                LogTransferException(e, "upload file");
                if (e is AggregateException && e.InnerException != null && (e.InnerException is TaskCanceledException || e.InnerException is OperationCanceledException || e.InnerException is TransferSkippedException))
                {
                    return true;
                }
                else if (e is TaskCanceledException || e is OperationCanceledException || e is TransferSkippedException)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                CloseTransferJournal();
            }
        }

        protected async Task<bool> SyncLocalSingleFileToRemote(string fileName)
        {
            FileInfo file = new FileInfo(fileName);
            L.Info("{file} has size {size}", file.FullName, PrintBytes(file.Length, ""));
            if (string.IsNullOrEmpty(JournalFilePath))
            {
                JournalFilePath = file.FullName + ".azsj";
            }
            if (string.IsNullOrEmpty(SignatureFilePath))
            {
                SignatureFilePath = file.FullName + ".sig";
            }
            OpenTransferJournal();
            UploadOptions options = new UploadOptions();
            SingleTransferContext context = new SingleTransferContext(JournalStream);
            context.LogLevel = LogLevel.Informational;
            context.ProgressHandler = new SingleFileTransferProgressReporter(file);
            context.SetAttributesCallback = (destination) =>
            {
                DestinationBlob = destination as CloudBlob;
                DestinationBlob.Properties.ContentType = this.ContentType;
            };
            context.ShouldOverwriteCallback = (source, destination) =>
            {
                CloudBlob b = (CloudBlob)destination;
                bool o = this.Overwrite;
                if (o)
                {
                    L.Info("Overwriting existing blob {container}/{blob}.", DestinationContainerName, b.Name);
                }
                else
                {
                    L.Warn("Not overwriting existing blob {container}/{blob}.", DestinationContainerName, b.Name);
                }
                return o;
            };
            context.FileTransferred += Context_FileTransferred;
            context.FileFailed += Context_FileFailed;
            context.FileSkipped += Context_FileSkipped;
            try
            {
                DestinationBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, file.Name, BlobType.BlockBlob);
                SignatureBlob = await DestinationStorage.GetorCreateCloudBlobAsync(DestinationContainerName, DestinationBlob.Name + ".sig", BlobType.BlockBlob) as CloudBlockBlob;
                using (Operation azOp = L.Begin("Upload file and compute file signature"))
                {
                    Signature = new SingleFileSignature(file, DestinationBlob);
                    ComputeSignatureTask = Task.Factory.StartNew(() => Signature.Compute(), CT, TaskCreationOptions.None, TaskScheduler.Default);
                    TransferTask = TransferManager.UploadAsync(file.FullName, DestinationBlob, options, context, CT);
                    Task.WaitAll(TransferTask, ComputeSignatureTask);
                    if (TransferTask.Status == TaskStatus.RanToCompletion && ComputeSignatureTask.Status == TaskStatus.RanToCompletion)
                    {
                        L.Info("File signature has length {0} bytes.", Signature.ComputedSignature.Length);
                        azOp.Complete();
                    }
                    else
                    {
                        return false;
                    }
                }
            }
            catch (Exception e)
            {
                LogTransferException(e, "upload file and compute file signature");
                if (e is AggregateException && e.InnerException != null && (e.InnerException is TaskCanceledException || e.InnerException is OperationCanceledException || e.InnerException is TransferSkippedException))
                {
                    return true;
                }
                else if (e is TaskCanceledException || e is OperationCanceledException || e is TransferSkippedException)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }
            finally
            {
                CloseTransferJournal();
            }

            using (Operation azOp = L.Begin("Upload and write file signature to disk"))
            {
                SignatureFile = new FileInfo(file.FullName + ".sig");
                WriteSignatureTask = Task.Run(() => WriteSignatureToFile(), CT);
                try
                {
                    WriteSignatureTask.Wait();
                    if (WriteSignatureTask.Result)
                    {
                        UploadSignatureTask = UploadSignature();
                        if (await UploadSignatureTask)
                        {
                            azOp.Complete();
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Exception e)
                {
                    LogTransferException(e, "upload and write file signature to disk");
                    azOp.Cancel();
                    if (e is TaskCanceledException || e is OperationCanceledException)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
            }
        }

        protected bool WriteSignatureToFile()
        {
            bool wrote = false;
            using (Operation azOp = L.Begin("Write signature to file {file}.", SignatureFile.FullName))
            {
                try
                {
                    if (SignatureFile.Exists)
                    {
                        L.Warn("The existing signature file {file} will be overwritten.", SignatureFile.FullName);
                    }
                    SignatureStream = SignatureFile.Open(FileMode.Create, FileAccess.Write, FileShare.Read);
                    BinaryFormatter formatter = new BinaryFormatter();
                    formatter.Serialize(SignatureStream, Signature);
                    SignatureStream.Flush();
                    SignatureFile.Refresh();
                    wrote = true;
                    azOp.Complete();
                }
                catch (Exception e)
                {
                    LogTransferException(e, $"write signature to file {SignatureFile.FullName}");
                    wrote = false;
                }
                finally
                {
                    if (SignatureStream != null)
                    {
                        SignatureStream.Dispose();
                    }
                }
            }
            return wrote;
        }

        protected async Task<bool> UploadSignature()
        {
            
            UploadOptions options = new UploadOptions();
            SingleTransferContext context = new SingleTransferContext();
            bool transferred = false;
            context.LogLevel = LogLevel.Informational;
            context.ProgressHandler = new SingleFileTransferProgressReporter(SignatureFile);
            context.SetAttributesCallback = (destination) =>
            {
                SignatureBlob = destination as CloudBlockBlob;
                SignatureBlob.Properties.ContentType = "application/octet-stream";
            };
            context.ShouldOverwriteCallback = (source, destination) =>
            {
                return true;
            };
            context.FileTransferred += (sender, e) =>
            {
                Context_FileTransferred(sender, e);
                transferred = true;
            };
            context.FileFailed += (sender, e) =>
            {
                Context_FileFailed(sender, e);
                transferred = false;
            };
            context.FileSkipped += (sender, e) =>
            {
                Context_FileSkipped(sender, e);
                transferred = false;
            };

            try
            {
                if (await SignatureBlob.ExistsAsync())
                {
                    L.Warn("The existing signature blob {blob} will be overwritten.", SignatureBlob.Name);
                }
                await TransferManager.UploadAsync(SignatureFile.FullName, SignatureBlob, options, context, CT);
                transferred = true; 
            }
            catch (Exception e)
            {
                LogTransferException(e, $"upload signature to cloud blob {SignatureBlob.Name}");
                if (e is OperationCanceledException || e is TaskCanceledException || (e is TransferException && CT.IsCancellationRequested))
                {
                    transferred = true;
                }
                else
                {
                    transferred = false;
                }
            }
            return transferred;
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
            return await Task.FromResult(true);
        }

        
 

        protected byte[] ComputeSignature(FileInfo file)
        {
            SignatureBuilder builder = new SignatureBuilder();
            using (FileStream basisStream = file.OpenRead())
            using (MemoryStream signatureStream = new MemoryStream())
            {
                builder.Build(basisStream, new SignatureWriter(signatureStream));
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
                    if (NoJournal || DeleteJournal)
                    {
                        JournalFile.Delete();
                        L.Info("Deleted existing journal file {file}.", JournalFilePath);
                        if (!NoJournal)
                        {
                            JournalFile = new FileInfo(JournalFilePath);
                            JournalStream = JournalFile.Open(FileMode.CreateNew, FileAccess.ReadWrite);
                            L.Info("Created new journal file {file}.", JournalFile.FullName);
                        }
                        else
                        {
                            JournalStream = new MemoryStream();
                        }
                    }
                    else
                    {
                        JournalStream = JournalFile.Open(FileMode.Open, FileAccess.ReadWrite);
                        L.Info("Resuming transfer from journal file {file}.", JournalFile.FullName);
                    }
                }
                else
                {
                    JournalStream = JournalFile.Open(FileMode.CreateNew, FileAccess.ReadWrite);
                    L.Info("Created journal file {file} for transfer.", JournalFile.FullName);
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

        protected void LogTransferException(Exception e, string operationDescription)
        {
            if (e is AggregateException && e.InnerException != null)
            {
                e = e.InnerException;
            }
            if (e is TaskCanceledException || e is OperationCanceledException)
            {
                L.Info($"The {operationDescription} operation was cancelled.");
            }
            else if (e is TransferSkippedException)
            {
                return;
            }
            else if (e is StorageException)
            {
                L.Error(e, $"A storage error occured during the {operationDescription} operation.");
            }
            else if (e is TransferException)
            {
                if (CT.IsCancellationRequested)
                {
                    L.Info($"The {operationDescription} operation was cancelled.");
                }
                else
                {
                    L.Error(e, $"A transfer error occured during the {operationDescription} operation.");
                }
            }
            else if (e is IOException)
            {
                L.Error(e, $"An I/O error occurred during the {operationDescription} operation.");
            }
            else if (e is SerializationException)
            {
                L.Error(e, $"An serialization error occurred during the {operationDescription} operation.");
            }
            else
            {
                L.Error(e, $"An unknown error occurred during the {operationDescription} operation.");
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
            L.Info("Transfer of file {file} completed in {millisec} ms.", e.Source, (e.EndTime - e.StartTime).TotalMilliseconds);
        }

        public static string PrintBytes(double bytes, string suffix)
        {
            if (bytes >= 0 && bytes <= 1024)
            {
                return string.Format("{0:N0} B{1}", bytes, suffix);
            }
            else if (bytes >= 1024 && bytes < (1024 * 1024))
            {
                return string.Format("{0:N1} KB{1}", bytes / 1024, suffix);
            }
            else if (bytes >= (1024 * 1024) && bytes < (1024 * 1024 * 1024))
            {
                return string.Format("{0:N1} MB{1}", bytes / (1024 * 1024), suffix);
            }
            else if (bytes >= (1024 * 1024 * 1024))
            {
                return string.Format("{0:N1} GB{1}", bytes / (1024 * 1024 * 1024), suffix);
            }
            else throw new ArgumentOutOfRangeException();

        }

        public static Tuple<double, string> PrintBytesToTuple(double bytes, string suffix)
        {
            string[] s = PrintBytes(bytes, suffix).Split(' ');
            return new Tuple<double, string>(Double.Parse(s[0]), s[1]);
        }

        #endregion

        #region Properties
        public bool Initialised { get; protected set; } = false;
        public IConfigurationRoot AppConfig { get; protected set; }
        public CancellationToken CT { get; protected set; }
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
        public CloudBlockBlob SignatureBlob { get; protected set; }
        public Signature Signature { get; protected set; }
        public Stream SignatureStream { get; protected set; } = new MemoryStream();
        public string SignatureFilePath { get; protected set; }
        public FileInfo SignatureFile { get; protected set; }
        public Task<bool> WriteSignatureTask { get; protected set; }
        public Task ComputeSignatureTask { get; protected set; }
        public Task<bool> UploadSignatureTask { get; protected set; }

        #endregion

        #region Fields
        protected Logger<TransferEngine> L = new Logger<TransferEngine>();
        #endregion
    }
}
