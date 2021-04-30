//#define NOAUTH
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AzureFunctions.OidcAuthentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TwoWeeksReady.Common;

namespace TwoWeeksReady.Photos
{
  public class PhotoFunction : BaseFunction
  {

    const string CONTAINERNAME = "photos";
    const string USER_METADATA_KEY = "HTBOX_USER_PRINCIPAL";

    public PhotoFunction(IApiAuthentication apiAuthentication) :
      base(apiAuthentication)
    {
    }

    [FunctionName("photo")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", "get", "delete", Route = "photo/{id?}")]
        HttpRequest req,
        ILogger log)
    {
      log.LogInformation("Attempting to upload image.");

#if !NOAUTH
      if (!await Authorized(req, log)) return new UnauthorizedResult();
#endif
      switch (req.Method.ToLower())
      {
        case "get":
          return await GetPhoto(req, log);
        case "post":
          return await PostPhoto(req, log);
        case "delete":
          return await DeletePhoto(req, log);
        default:
          return new BadRequestObjectResult("Invalid operation requested.");
      }
    }

    async Task<IActionResult> GetPhoto(HttpRequest req, ILogger log)
    {
      try
      {
        // Get Blob
        var containerClient = GetContainerClient();
        var blobName = req.Path.Value.Split("/").Last();
        var blob = containerClient.GetBlobClient(blobName);

        if (!await blob.ExistsAsync()) return new NotFoundResult();

#if !NOAUTH
        // Assert that this user has access ot the blog
        var props = await blob.GetPropertiesAsync();
        if (props is null || props.Value.Metadata[USER_METADATA_KEY] != this.Principal.Identity.Name)
        {
          return new UnauthorizedResult();
        }
#endif

        await blob.DownloadToAsync(req.HttpContext.Response.Body);

        return new OkObjectResult(req.HttpContext.Response);

      }
      catch (Exception ex)
      {
        log.LogError($"Failed to get photo: {ex}");
      }

      return new NotFoundResult();
    }

    async Task<IActionResult> PostPhoto(HttpRequest req,
        ILogger log)
    {

      try
      {
        // Load and Resize
        using (var image = ResizeImage(req.Body))
        {
          // Load the configuration to the blob service
          var containerClient = GetContainerClient();
          var blogContainer = await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);

          // Create blog id (unique)
          var blobName = $"{Guid.NewGuid().ToString()}.jpg";

          // Get the client for this new blob
          var blobClient = containerClient.GetBlobClient(blobName);

#if !NOAUTH
          // Add the userid to the metadata
          var metadata = new Dictionary<string, string>();
          metadata.Add(USER_METADATA_KEY, Principal.Identity.Name);
          await blobClient.SetMetadataAsync(metadata);
#endif

          // Upload the image
          var response = await blobClient.UploadAsync(image);
          if (response != null)
          {
            // Add content type
            await blobClient.SetHttpHeadersAsync(new BlobHttpHeaders() { ContentType = "image/jpg" });

            var url = GenerateBlobUrl(req, blobClient);

            return new CreatedResult(url, url);
          }

        }

      }
      catch (Exception ex)
      {
        log.LogError($"Failed to save image: {ex}");
      }

      return new BadRequestObjectResult("Failed to save image.");
    }

    private static BlobContainerClient GetContainerClient()
    {
      var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings:StorageConnection");
      var blobServiceClient = new BlobServiceClient(connectionString);

      // Get the container
      var containerClient = blobServiceClient.GetBlobContainerClient(CONTAINERNAME);
      return containerClient;
    }

    Uri GenerateBlobUrl(HttpRequest req, BlobClient blobClient)
    {
      var http = req.IsHttps ? "https" : "http";
      return new Uri($"{http}://{req.Host.Value}/api/photo/{blobClient.Name}");
    }

    async Task<IActionResult> DeletePhoto(HttpRequest req, ILogger log)
    {
      try
      {
        var containerClient = GetContainerClient();
        var blobName = req.Path.Value.Split("/").Last();
        var blob = containerClient.GetBlobClient(blobName);

        if (!await blob.ExistsAsync()) return new NotFoundResult();

        var result = await blob.DeleteAsync();
        if (result.Status == 200)
        {
          return new OkResult();
        }
        else
        {
          return new BadRequestObjectResult("Failed to delete existing photo.");
        }
      }
      catch (Exception ex)
      {
        log.LogError($"Failed to delete photo: {ex}");
      }

      return new BadRequestResult();
    }

    MemoryStream ResizeImage(Stream stream)
    {
      MemoryStream outStream = new MemoryStream();

      using (var image = Image.Load(stream))
      {
        // Determine which way to size it
        var size = new Size(800, 800);

        // Determine how to resize
        var options = new ResizeOptions()
        {
          Mode = ResizeMode.Max,
          Size = new Size(size.Width, size.Height)
        };

        // Load, resize, set the format, and quality and save an image.
        image.Mutate(x => x
          .Resize(options)
          .BackgroundColor(new Rgba32(255, 255, 255, 255)));

        image.Save(outStream, new JpegEncoder() { Quality = 70 });

        // Reset for reading
        outStream.Position = 0;

        return outStream;
      }

    }
  }
}

