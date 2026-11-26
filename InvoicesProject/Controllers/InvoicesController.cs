using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace InvoicesProject.Controllers
{
    [ApiController]
    [Route("invoices")]
    public class InvoicesController : ControllerBase
    {

        private readonly IConfiguration _config;
        private readonly ILogger<InvoicesController> _logger;
        
        public InvoicesController(IConfiguration config,ILogger<InvoicesController> logger)  
        { 
            _config = config;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            //validate the file

            if (file == null)
            {
                return BadRequest("File is empty.");
            }

            if (file.Length == 0)
            {
                return BadRequest("File content is empty.");
            }

            using var stream = file.OpenReadStream();
            byte[] buffer = new byte[4];

            
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read < 4) return BadRequest("Only PDF are allowed.");

            bool isPdf =
                buffer[0] == 0x25 && // %
                buffer[1] == 0x50 && // P
                buffer[2] == 0x44 && // D
                buffer[3] == 0x46;   // F

            if (!isPdf)
            {
                return BadRequest("Only PDF are allowed.");
            }


            //upload to blob storage
            var storageConnString = _config["Storage:ConnectionString"];
            var containerName = _config["Storage:ContainerName"];
            var sqlConnString = _config.GetConnectionString("Sql");

            var container = new BlobContainerClient(storageConnString,containerName);
            await container.CreateIfNotExistsAsync();

            var id = Guid.NewGuid();

            var blobName = $"{id}.pdf";
            var blob = container.GetBlobClient(blobName);

            stream.Position = 0;
            await blob.UploadAsync(stream);

            //insert row in sql
            try {            
            await using var conn = new SqlConnection(sqlConnString);
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(
                " INSERT INTO Invoices (Id, FileName, BlobName, Status, UploadedAt )" +
                " VALUES (@id, @fn, @bn, 'Uploaded', SYSUTCDATETIME()) ", conn);

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@fn", file.FileName);
            cmd.Parameters.AddWithValue("@bn", blobName);

            await cmd.ExecuteNonQueryAsync();
            }

            catch (Exception ex)
            {
                _logger.LogError(ex, "SQL insert failed for blob {BlobName}", blobName);
                
                try
                {
                    await blob.DeleteIfExistsAsync();
                }

                catch (Exception cleanupEx) 
                {
                    _logger.LogError(cleanupEx, "Cleanup failed, orphaned blob {blobName}", blobName);
                    
                
                }
                
                return StatusCode(500,"Could not save invoice. Please try again.");
            }

            // return a response
            return Created($"/invoices/{id}", new {id, blobName, status = "Uploaded"});
        }
    }
}
