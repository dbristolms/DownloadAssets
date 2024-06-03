# DownloadAssets
**This application downloads all assets from an Azure Media Services account.**

This application was written in C#.NET. It uses the Azure Media Services [v3 API](https://docs.microsoft.com/en-us/azure/media-services/latest/media-services-apis-overview).

Azure Media Services is being deprecated 30-June 2024 as per the [deprecation announcement](https://aka.ms/ams-retirement).  Ninety days after the service is turned off, accounts will be deleted. The Azure Storage account that backs the Media Services account will still contain the Storage containers and blobs from the Media Services assets. These blobs & containers will not be deleted when Media Services is deleted.  However, if you wish to download all the video assets to your local computer you can do so with this application.

The application works by enumerating through the AMS asset collection, locates the Azure Storage container for each asset, and downloads the container and the container’s blobs (files) to a local folder.

You'll need to update the appsettings.json with your AMS account name, subscription ID, resource group, and Storage connection string.  And a local directory for where to put the files.

The downloads are multi-threaded for the blobs within an asset. If the download has to be stopped, the next time it is run it will recheck all assets and download only the blobs that do not match the size specificed in Azure Storage. Essentially this makes the download resumable.
