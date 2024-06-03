namespace DownloadAssets
{
    using Azure.Identity;
    using Azure.ResourceManager;
    using Azure.ResourceManager.Media;
    using Azure.Storage;
    using Azure.Storage.Blobs;
    using Azure.Storage.Blobs.Models;
    using Microsoft.Extensions.Configuration;
    using System.ComponentModel.DataAnnotations;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Threading.Tasks;

    internal class Program
    {

        static async Task Main()
        {
            // Loading the settings from the appsettings.json file or from the command line parameters
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            if (!Options.TryGetOptions(configuration, out var options))
            {
                return;
            }

            Console.WriteLine($"Subscription ID:             {options.AZURE_SUBSCRIPTION_ID}");
            Console.WriteLine($"Resource group name:         {options.AZURE_RESOURCE_GROUP}");
            Console.WriteLine($"Media Services account name: {options.AZURE_MEDIA_SERVICES_ACCOUNT_NAME}");
            //Console.WriteLine($"Storage account name: {options.AZURE_STORAGE_ACCOUNT_NAME}");
            Console.WriteLine($"Local download location: {options.LOCAL_DOWNLOAD_FOLDER}");
            Console.WriteLine();

            try
            {
                if (!Directory.Exists(options.LOCAL_DOWNLOAD_FOLDER))
                    Directory.CreateDirectory(options.LOCAL_DOWNLOAD_FOLDER);
            }
            catch 
            {
                Console.WriteLine("You need to specifiy a valid local directory in the appsettings.json file.");
                return;
            }

            // Authenticate to Azure Media Services
            var mediaServiceAccountId = MediaServicesAccountResource.CreateResourceIdentifier(
               subscriptionId: options.AZURE_SUBSCRIPTION_ID.ToString(),
               resourceGroupName: options.AZURE_RESOURCE_GROUP,
               accountName: options.AZURE_MEDIA_SERVICES_ACCOUNT_NAME);
            DefaultAzureCredential credential = new(includeInteractiveCredentials: true);
            ArmClient armClient = new(credential);
            MediaServicesAccountResource mediaServicesAccount = armClient.GetMediaServicesAccountResource(mediaServiceAccountId);

            // Use the Azure Storage connection string to authenticate
            var blobServiceClient = new BlobServiceClient(options.AZURE_STORAGE_ACCOUNT_CONNECTION_STRING);

            // List all assets in the account
            Console.WriteLine("Listing all the assets in this account");
            await foreach (MediaAssetResource asset in mediaServicesAccount.GetMediaAssets().GetAllAsync())
            {
                BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient(asset.Data.Container);
                Console.WriteLine($" - {asset.Data.Name}  \n      ID: {asset.Data.AssetId}");
                Console.WriteLine("      Container URI {0}", containerClient.Uri);

                // Determine if the asset is a live archive. If so, skip it.
                if (await IsLiveArchive(containerClient))
                {
                    Console.WriteLine("***Asset is a live archive.  Download not available.***");
                }
                else
                {
                    // Create new local folder for the container to download to
                    string downloadContainerFolder;
                    downloadContainerFolder = options.LOCAL_DOWNLOAD_FOLDER + "\\" + asset.Data.Container;
                    if (!Directory.Exists(downloadContainerFolder))
                        Directory.CreateDirectory(downloadContainerFolder);

                    // Download all blobs within the container
                    await DownloadBlobToFileAsync(containerClient, downloadContainerFolder);
                }
            }
        }

        private static async Task<bool> IsLiveArchive(BlobContainerClient container)
        {
            // Azure Media Services uses directories in a Storage container to hold all of the
            // fragblobs that are the live segments.  This tool does not support live archives.
            string prefix = "/";
            // Get a list of blob information
            Azure.AsyncPageable<BlobHierarchyItem> results = container.GetBlobsByHierarchyAsync();
            await foreach (var item in results)
            {
                // Check to see if the name contains a slash.  If so then this is a live archive.
                if (item.Blob.Name.Contains(prefix))
                {
                    return true;
                }
            }   
            return false;
        }

        private static async Task DownloadBlobToFileAsync(BlobContainerClient container, string downloadContainerFolder)
        {
            // Setup the status tracker first
            ContainerDownloadStatus dlStatus = new();

            // Setup the list of download tasks
            List<Task> downloadTasks = [];
            // Enumerate through all blobs in the container
            await foreach (BlobItem blobItem in container.GetBlobsAsync())
            {
                bool alreadyDownloaded = CheckIfAlreadyDownloaded(blobItem, downloadContainerFolder);
                if (!alreadyDownloaded)
                {
                    // Kick off a download task for each blob, but don't wait for the download to complete before
                    // starting the next download
                    downloadTasks.Add(DownloadBlobAsync(container, blobItem.Name, downloadContainerFolder, dlStatus));
                }
                else
                {
                    Console.WriteLine("          Blob '{0}' already downloaded.", blobItem.Name);
                }
            }
            // Wait for all downloads to complete for this container
            await Task.WhenAll(downloadTasks);
        }

        private static bool CheckIfAlreadyDownloaded(BlobItem blobItem, string downloadContainerFolder)
        {
            string fullLocalPath = downloadContainerFolder +"\\" + blobItem.Name;
            if (File.Exists(fullLocalPath))
            {
                long localLength = new FileInfo(fullLocalPath).Length;
                if (blobItem.Properties.ContentLength == localLength)
                {                    
                    return true;
                }
            }
            return false;
        }

        static async Task DownloadBlobAsync(BlobContainerClient containerClient, string blobName, string downloadContainerFolder, ContainerDownloadStatus containerDownloadStatus)
        {
            BlobClient blobClient = containerClient.GetBlobClient(blobName);
            Azure.Response<BlobProperties> properties = await blobClient.GetPropertiesAsync();
            long blobSize = properties.Value.ContentLength;

            // Add the blob to the list of blobs
            containerDownloadStatus.AddFile(blobName, blobSize, 0);

            string localFilePath = $"{downloadContainerFolder}/{blobName}"; // Specify your local path

            using (FileStream fs = File.OpenWrite(localFilePath))
            {
                // Define progress handler
                IProgress<long> progressHandler = new Progress<long>(bytesDownloaded =>
                {
                    try
                    {
                        int index = containerDownloadStatus.blobs.FindIndex(blobItem => blobItem.Name == blobName);
                        containerDownloadStatus.blobs[index].DownloadedBytes = bytesDownloaded;
                        Console.Write("\rContainer is {0}% complete.", containerDownloadStatus.PercentComplete().ToString("0.0"));
                    }
                    catch
                    {
                        Console.WriteLine("Couldn't update the download status");
                    }
                });

                DownloadTransferValidationOptions validationOptions = new()
                {
                    AutoValidateChecksum = true,
                    ChecksumAlgorithm = StorageChecksumAlgorithm.Auto
                };

                BlobDownloadToOptions downloadOptions = new()
                {
                    TransferValidation = validationOptions,
                    ProgressHandler = progressHandler
                };
                await blobClient.DownloadToAsync(fs, downloadOptions);
            }
            Console.WriteLine($"\r          Downloaded blob: {blobName}");
        }
    }

    public class BlobDownload
    {
        public string Name { get; set; }
        public long FileSize { get; set; }
        public long DownloadedBytes { get; set; }
    }

    public class ContainerDownloadStatus
    {
        public List<BlobDownload> blobs = [];

        public void AddFile(string name, long fileSize, long downloadedBytes)
        {
            blobs.Add(new BlobDownload { Name = name, FileSize = fileSize, DownloadedBytes = downloadedBytes });
        }

        private long TotalFileSize()
        {
            return blobs.Sum(file => file.FileSize);
        }

        private long TotalDownloadedBytes()
        {
            return blobs.Sum(file => file.DownloadedBytes);
        }

        public double PercentComplete()
        {
            return Math.Round((double)TotalDownloadedBytes() / TotalFileSize() * 100, 1);
        }
    }
    internal class Options
    {
        [Required]
        public Guid? AZURE_SUBSCRIPTION_ID { get; set; }

        [Required]
        public string? AZURE_RESOURCE_GROUP { get; set; }

        [Required]
        public string? AZURE_MEDIA_SERVICES_ACCOUNT_NAME { get; set; }

        [Required]
        public string? AZURE_STORAGE_ACCOUNT_CONNECTION_STRING { get; set; }

        [Required]
        public string? LOCAL_DOWNLOAD_FOLDER { get; set; }

        static public bool TryGetOptions(IConfiguration configuration, [NotNullWhen(returnValue: true)] out Options? options)
        {
            try
            {
                options = configuration.Get<Options>() ?? throw new Exception("No configuration found. Configuration can be set in appsettings.json or using command line options.");
                Validator.ValidateObject(options, new ValidationContext(options), true);
                return true;
            }
            catch (Exception ex)
            {
                options = null;
                Console.WriteLine(ex.Message);
                return false;
            }
        }
    }
}