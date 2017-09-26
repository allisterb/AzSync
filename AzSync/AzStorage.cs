﻿//---------------------------------------------------------------------------------------------------------------------------------------
// Based on Util.cs from Azure Storage Data Movement Library for .Net: https://github.com/Azure/azure-storage-net-data-movement 
//    Copyright (c) Microsoft Corporation
//---------------------------------------------------------------------------------------------------------------------------------------
namespace AzSync
{
    using System;
    using System.Text;
    using System.Threading.Tasks;

    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Auth;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Microsoft.WindowsAzure.Storage.File;
    using Microsoft.WindowsAzure.Storage.RetryPolicies;

    using Serilog;
    using SerilogTimings;
    using SerilogTimings.Extensions;

    public class AzStorage : ILogging
    {
        #region Constructors
        public AzStorage(SyncEngine engine, string connString, bool rethrowExceptions = false)
        {
            this.Engine = engine;
            this.ConnectionString = connString;
            this.RethrowExceptions = rethrowExceptions;
            GetStorageAccount();
            if (this.StorageAccount != null)
            {
                this.Initialised = true;
            }
        }
        #endregion

        #region Properties
        public SyncEngine Engine { get; protected set; }
        public string ConnectionString { get; protected set; }
        public CloudStorageAccount StorageAccount { get; protected set; }
        public CloudBlobClient BlobClient { get; protected set; }
        public CloudFileClient FileClient { get; protected set; }
        public bool Initialised { get; protected set; } = false;
        public bool RethrowExceptions { get; protected set; }
        #endregion

        #region Methods
        public static string GetConnectionString(Uri endPointUrl, string accountKey)
        {
            if (endPointUrl.Segments.Length < 2)
            {
                Log.Logger.Error("endPointUrl {u} does not have the correct path segments", endPointUrl.ToString());
                return string.Empty;
            }
            StringBuilder csb = new StringBuilder();
            csb.AppendFormat("DefaultEndpointsProtocol={0};AccountName={1};AccountKey={2};BlobEndpoint={3};", endPointUrl.Scheme, endPointUrl.Segments[1], accountKey, endPointUrl.Scheme + "://" + endPointUrl.Authority);
            return csb.ToString();
        }

        /// <summary>
        /// Get a CloudBlob instance with the specified name and type in the given container or create a reference to a new blob.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="blobName">Blob name.</param>
        /// <param name="blobType">Type of blob.</param>
        /// <returns>A <see cref="Task{T}"/> object of type <see cref="CloudBlob"/> that represents the asynchronous operation.</returns>
        public async Task<CloudBlob> GetorCreateCloudBlobAsync(string containerName, string blobName, BlobType blobType, DateTimeOffset? snapshotTime = null)
        { using (Operation azOp = L.Begin("Get Azure Storage blob {0}/{1}", containerName, blobName))
            {
                try
                {
                    CloudBlobClient client = GetCloudBlobClient();
                    CloudBlobContainer container = client.GetContainerReference(containerName);
                    await container.CreateIfNotExistsAsync();

                    CloudBlob cloudBlob;
                    switch (blobType)
                    {
                        case BlobType.AppendBlob:
                            cloudBlob = container.GetAppendBlobReference(blobName, snapshotTime);
                            break;
                        case BlobType.BlockBlob:
                            cloudBlob = container.GetBlockBlobReference(blobName, snapshotTime);
                            break;
                        case BlobType.PageBlob:
                            cloudBlob = container.GetPageBlobReference(blobName, snapshotTime);
                            break;
                        case BlobType.Unspecified:
                        default:
                            throw new ArgumentException(string.Format("Invalid blob type {0}", blobType.ToString()), "blobType");
                    }
                    azOp.Complete();
                    return cloudBlob;
                }
                catch (StorageException se)
                {
                    L.Error(se, "Storage exception thrown getting Azure Storage blob {bn} from container {cn}.", blobName, containerName);
                    if (RethrowExceptions)
                    {
                        throw se;
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (Exception e)
                {
                    L.Error(e, "Exception thrown getting Azure Storage blob {bn} from container {cn}.", blobName, containerName);
                    if (RethrowExceptions)
                    {
                        throw e;
                    }
                    else
                    {
                        return null;
                    }
                }
            }

        }

        /// <summary>
        /// Get a CloudBlobDirectory instance with the specified name in the given container.
        /// </summary>
        /// <param name="containerName">Container name.</param>
        /// <param name="directoryName">Blob directory name.</param>
        /// <returns>A <see cref="Task{T}"/> object of type <see cref="CloudBlobDirectory"/> that represents the asynchronous operation.</returns>
        public async Task<CloudBlobDirectory> GetCloudBlobDirectoryAsync(string containerName, string directoryName)
        {
            using (Operation azOp = L.Begin("Get Azure Storage directory"))
            {
                try
                {
                    CloudBlobClient client = GetCloudBlobClient();
                    CloudBlobContainer container = client.GetContainerReference(containerName);
                    await container.CreateIfNotExistsAsync();
                    CloudBlobDirectory dir = container.GetDirectoryReference(directoryName);
                    azOp.Complete();
                    return dir;
                }
                catch (StorageException se)
                {
                    L.Error(se, "Storage exception thrown getting Azure Storage directory {dn} from container {cn}.", directoryName, containerName);
                    if (RethrowExceptions)
                    {
                        throw se;
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (Exception e)
                {
                    L.Error(e, "Exception thrown getting Azure Storage directory {dn} from container {cn}.", directoryName, containerName);
                    if (RethrowExceptions)
                    {
                        throw e;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>
        /// Get a CloudFile instance with the specified name in the given share.
        /// </summary>
        /// <param name="shareName">Share name.</param>
        /// <param name="fileName">File name.</param>
        /// <returns>A <see cref="Task{T}"/> object of type <see cref="CloudFile"/> that represents the asynchronous operation.</returns>
        public async Task<CloudFile> GetCloudFileAsync(string shareName, string fileName)
        {
            using (Operation azOp = L.Begin("Get Azure Storage file share"))
            {
                try
                {
                    CloudFileClient client = GetCloudFileClient();
                    CloudFileShare share = client.GetShareReference(shareName);
                    await share.CreateIfNotExistsAsync();
                    CloudFileDirectory rootDirectory = share.GetRootDirectoryReference();
                    CloudFile file = rootDirectory.GetFileReference(fileName);
                    azOp.Complete();
                    return file;

                }
                catch (StorageException se)
                {
                    L.Error(se, "Storage exception thrown getting Azure Storage file {fn} from share {sn}.", fileName, shareName);
                    if (RethrowExceptions)
                    {
                        throw se;
                    }
                    else
                    {
                        return null;
                    }
                }
                catch (Exception e)
                {
                    L.Error(e, "Exception thrown getting Azure Storage file {fn} from share {sn}.", fileName, shareName);
                    if (RethrowExceptions)
                    {
                        throw e;
                    }
                    else
                    {
                        return null;
                    }
                }
            }
        }

        /// <summary>
        /// Delete the share with the specified name if it exists.
        /// </summary>
        /// <param name="shareName">Name of share to delete.</param>
        public async Task DeleteShareAsync(string shareName)
        {
            using (Operation azOp = L.Begin("Delete Azure Storage share"))
            {
                try
                {
                    CloudFileClient client = GetCloudFileClient();
                    CloudFileShare share = client.GetShareReference(shareName);
                    await share.DeleteIfExistsAsync();
                    azOp.Complete();
                }
                catch (Exception e)
                {
                    L.Error(e, "Exception throw deleting Azure Storage share {sn}.", shareName);
                }
            }
        }

        /// <summary>
        /// Delete the container with the specified name if it exists.
        /// </summary>
        /// <param name="containerName">Name of container to delete.</param>
        public async Task DeleteContainerAsync(string containerName)
        {
            using (Operation azOp = L.Begin("Delete Azure Storage container"))
            {
                try
                {
                    CloudBlobClient client = GetCloudBlobClient();
                    CloudBlobContainer container = client.GetContainerReference(containerName);
                    await container.DeleteIfExistsAsync();
                    azOp.Complete();
                }
                catch (Exception e)
                {
                    L.Error(e, "Exception throw deleting Azure Storage container {c}.", containerName);
                }
            }
        }

        private CloudBlobClient GetCloudBlobClient()
        {
            if (BlobClient == null)
            {
                BlobClient = GetStorageAccount().CreateCloudBlobClient();
                BlobClient.DefaultRequestOptions.RetryPolicy = new LinearRetry(TimeSpan.FromSeconds(Engine.RetryTime), Engine.RetryCount);
            }
            return BlobClient;
        }

        private CloudFileClient GetCloudFileClient()
        {
            if (FileClient == null)
            {
                FileClient = GetStorageAccount().CreateCloudFileClient();
            }

            return FileClient;
        }

        private CloudStorageAccount GetStorageAccount()
        {
            try
            {
                if (StorageAccount == null)
                {
                    StorageAccount = CloudStorageAccount.Parse(ConnectionString);
                }

                return StorageAccount;
            }
            catch (Exception e)
            {
                L.Error(e, "Exception throw parsing Azure connection string {cs}.", ConnectionString);
                return null;
            }

        }
        #endregion

        #region Fields
        private Logger<AzStorage> L = new Logger<AzStorage>();
        #endregion
    }
}