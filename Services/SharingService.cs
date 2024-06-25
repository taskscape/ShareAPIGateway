using System.Text.RegularExpressions;
using Markdig;

namespace ShareAPI.Services;

public partial class SharingService : ISharingService
{
    private readonly ILogger<SharingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly LinkGenerator _linkGenerator;
    private readonly IHttpContextAccessor _httpContextAccessor;
    public required string? NotesFolderPath;
    public required string? TemplatePath;
    public required string? TemplateScriptPath;
    public required string? BuildVersion;

    public SharingService(ILogger<SharingService> logger, IConfiguration configuration, LinkGenerator linkGenerator, IHttpContextAccessor httpContextAccessor)
    {
        _logger = logger;
        _configuration = configuration;
        _linkGenerator = linkGenerator;
        _httpContextAccessor = httpContextAccessor;
        NotesFolderPath = _configuration.GetSection("NotesFolder").Value ?? string.Empty;
        TemplatePath = _configuration.GetSection("TemplatePath").Value ?? string.Empty;
        TemplateScriptPath = _configuration.GetSection("TemplateScriptPath").Value ?? string.Empty;
        BuildVersion = _configuration.GetSection("version").Value ?? string.Empty;
    }

    public async Task<Guid> UploadMarkdownWithFiles(string markdown, List<IFormFile> files)
    {
        var identifier = Guid.NewGuid();

        // Create a directory with the GUID as its name
        string directoryPath = Path.Combine(NotesFolderPath, identifier.ToString());
        try
        {
            Directory.CreateDirectory(directoryPath);
        }
        catch (Exception e)
        {
            _logger.LogError($"Error creating directory: {e}");
            return Guid.Empty;
        }

        await SaveFiles(markdown, files, directoryPath);
        return identifier;
    }

    public Task<string> GetMarkdownContent(string identifier)
    {
        // Construct the path to the markdown file using the provided GUID
        string markdownFilePath = Path.Combine(NotesFolderPath, identifier, "content.md");

        // Check if the file exists
        if (!File.Exists(markdownFilePath))
        {
            return Task.FromResult<string>(null);
        }

        // Read the content of the markdown file
        string markdownContent = File.ReadAllText(markdownFilePath);

        // Convert markdown string to HTML
        string htmlContent = Markdown.ToHtml(markdownContent);

        // Reading templates
        string? templatePath = TemplatePath;
        string? scriptTemplatePath = TemplateScriptPath;
        string htmlTemplate = File.ReadAllText(templatePath);
        string scriptTemplate = File.ReadAllText(scriptTemplatePath);

        // Inserting markdown content into html
        htmlTemplate = htmlTemplate.Replace("{markdown}", htmlContent);

        // Getting PDFs
        IEnumerable<string> pdfFiles = Directory.GetFiles(Path.Combine(NotesFolderPath, identifier))
            .Where(file => Path.GetExtension(file) == ".pdf");

        IEnumerable<string> pdfUrls = pdfFiles.Select(file =>
        {
            string fileName = Path.GetFileName(file);
            string? url = _linkGenerator.GetPathByAction("GetPdf", "sharing", new { identifier, fileName });
            string scheme = _httpContextAccessor.HttpContext.Request.Scheme;
            var host = _httpContextAccessor.HttpContext.Request.Host.ToString();
            return Uri.UnescapeDataString($"{scheme}://{host}{url}");
        });
        
        Regex regex = FileAttachmentRegex();
        // Adding PDFs to template
        foreach (string pdfUrl in pdfUrls)
        {
            scriptTemplate = scriptTemplate.Replace("{pdfUrl}", $"\"{pdfUrl}\"");
            scriptTemplate = scriptTemplate.Replace("{pdfDivId}", $"{Guid.NewGuid()}");
            scriptTemplate = scriptTemplate.Replace("{pdfName}", $"\"{Path.GetFileName(pdfUrl)}\"");
            scriptTemplate = scriptTemplate.Replace("{token}", $"\"{_configuration.GetSection("AdobeAPIToken").Value}\"");
            MatchCollection matches = regex.Matches(htmlTemplate);
            foreach (Match match in matches)
            {
                // check if the match value contains the file name without the first 9 random characters
                if (!match.Value.Contains(Path.GetFileName(pdfUrl)[9..])) continue;
                htmlTemplate = ReplaceFirstOccurrence(htmlTemplate, match.Value, scriptTemplate);
                break;
            }
            
            scriptTemplate = File.ReadAllText(scriptTemplatePath);
        }

        // Getting docs
        IEnumerable<string> docFiles = Directory.GetFiles(Path.Combine(NotesFolderPath, identifier))
            .Where(file => Path.GetExtension(file) == ".doc" || Path.GetExtension(file) == ".docx");

        IEnumerable<string> docUrls = docFiles.Select(file =>
        {
            string fileName = Path.GetFileName(file);
            string? url = _linkGenerator.GetPathByAction("GetDoc", "sharing", new { identifier, fileName });
            string scheme = _httpContextAccessor.HttpContext.Request.Scheme;
            var host = _httpContextAccessor.HttpContext.Request.Host.ToString();
            return Uri.UnescapeDataString($"{scheme}://{host}{url}");
        });

        // Adding docs to template
        foreach (string docUrl in docUrls)
        {
            MatchCollection matches = regex.Matches(htmlTemplate);
            foreach (Match match in matches)
            {
                // check if the match value contains the file name without the first 9 random characters
                if (!match.Value.Contains(Path.GetFileName(docUrl)[9..])) continue;
                htmlTemplate = ReplaceFirstOccurrence(htmlTemplate, match.Value, $"<div class=\"docs-container\"><iframe src=\"https://docs.google.com/viewer?url={docUrl}&embedded=true\"></iframe></div>\n");
                break;
            }
        }
        var acceptedImageFormats = new List<string>
        {
            ".jpg",
            ".jpeg",
            ".png"
        };
        // Getting images
        IEnumerable<string> imageFiles = Directory.GetFiles(Path.Combine(NotesFolderPath, identifier))
            .Where(file => acceptedImageFormats.Contains(Path.GetExtension(file)));

        IEnumerable<string> imagePaths = imageFiles.Select(file =>
        {
            string fileName = Path.GetFileName(file);
            string? url = _linkGenerator.GetPathByAction("GetImage", "sharing", new { identifier, fileName });
            string scheme = _httpContextAccessor.HttpContext.Request.Scheme;
            var host = _httpContextAccessor.HttpContext.Request.Host.ToString();
            return $"{scheme}://{host}{url}";
        });

        // Adding images to template
        foreach (string imagePath in imagePaths)
        {
            MatchCollection matches = regex.Matches(htmlTemplate);
            foreach (Match match in matches)
            {
                // check if the match value contains the file name without the first 9 random characters
                if (!match.Value.Contains(Path.GetFileName(Uri.UnescapeDataString(imagePath))[9..])) continue;
                htmlTemplate = ReplaceFirstOccurrence(htmlTemplate, match.Value, $"<div class=\"image-container\"><img src={imagePath}></div>\n");
                break;
            }
        }
        
        var acceptedVideoFormats = new List<string>
        {
            ".mp4"
        };
        // Getting videos
        IEnumerable<string> videoFiles = Directory.GetFiles(Path.Combine(NotesFolderPath, identifier))
            .Where(file => acceptedVideoFormats.Contains(Path.GetExtension(file)));

        IEnumerable<string> videoPaths = videoFiles.Select(file =>
        {
            string fileName = Path.GetFileName(file);
            string? url = _linkGenerator.GetPathByAction("GetVideo", "sharing", new { identifier, fileName });
            string scheme = _httpContextAccessor.HttpContext.Request.Scheme;
            var host = _httpContextAccessor.HttpContext.Request.Host.ToString();
            return $"{scheme}://{host}{url}";
        });

        // Adding videos to template
        foreach (string videoPath in videoPaths)
        {
            MatchCollection matches = regex.Matches(htmlTemplate);
            foreach (Match match in matches)
            {
                // check if the match value contains the file name without the first 9 random characters
                if (!match.Value.Contains(Path.GetFileName(Uri.UnescapeDataString(videoPath))[9..])) continue;
                htmlTemplate = ReplaceFirstOccurrence(htmlTemplate, match.Value, $"<video class=\"video-container\" controls><source src={videoPath} type=\"video/mp4\"></video>\n");
                break;
            }
        }
        return Task.FromResult(htmlTemplate);
    }

    public async Task<bool> UpdateMarkdownWithFiles(string markdown, List<IFormFile> files, Guid identifier)
    {
        // Create a directory with the GUID as its name
        string directoryPath = Path.Combine(NotesFolderPath, identifier.ToString());
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }
        Directory.Delete(directoryPath, true);
        Directory.CreateDirectory(directoryPath);

        await SaveFiles(markdown, files, directoryPath);
        return true;
    }

    public Task<bool> DeleteMarkdownWithFiles(string identifier)
    {
        string directoryPath = Path.Combine(NotesFolderPath, identifier);

        if (!Directory.Exists(directoryPath))
        {
            return Task.FromResult(false);
        }

        Directory.Move(directoryPath, directoryPath + "-deleted");
        return Task.FromResult(true);
    }

    public Task<FileStream> GetPdf(string identifier, string fileName)
    {
        string filePath = Path.Combine(NotesFolderPath, identifier, fileName);

        return !File.Exists(filePath) ? Task.FromResult<FileStream>(null) : Task.FromResult(new FileStream(filePath, FileMode.Open, FileAccess.Read));
    }

    public Task<FileStream> GetDoc(string identifier, string fileName)
    {
        string filePath = Path.Combine(NotesFolderPath, identifier, fileName);

        return !File.Exists(filePath) ? Task.FromResult<FileStream>(null) : Task.FromResult(new FileStream(filePath, FileMode.Open, FileAccess.Read));
    }

    public Task<FileStream> GetImage(string identifier, string fileName)
    {
        string filePath = Path.Combine(NotesFolderPath, identifier, fileName);

        return !File.Exists(filePath) ? Task.FromResult<FileStream>(null) : Task.FromResult(new FileStream(filePath, FileMode.Open, FileAccess.Read));
    }
    
    public Task<FileStream> GetVideo(string identifier, string fileName)
    {
        string filePath = Path.Combine(NotesFolderPath, identifier, fileName);

        return !File.Exists(filePath) ? Task.FromResult<FileStream>(null) : Task.FromResult(new FileStream(filePath, FileMode.Open, FileAccess.Read));
    }

    public Task<object> GetVersion()
    {
        var versionInfo = new
        {
            Version = BuildVersion,
            CompilationDate = File.GetLastAccessTime(GetType().Assembly.Location)
        };
        return Task.FromResult((object)versionInfo);
    }

    private async Task SaveFiles(string markdown, List<IFormFile> files, string directoryPath)
    {
        // Save the markdown content to a file within this directory
        string markdownFilePath = Path.Combine(directoryPath, "content.md");
        await File.WriteAllTextAsync(markdownFilePath, markdown);

        _logger.LogInformation($"Markdown saved to: {markdownFilePath}");

        long totalBytes = 0;
        foreach (IFormFile file in files)
        {
            // Log the file name
            _logger.LogInformation($"Received file: {file.FileName}");

            // Save each file in the directory
            var random = new Random();
            var uniqueFileName = $"{random.Next():x}-{file.FileName}";
            string filePath = Path.Combine(directoryPath, uniqueFileName);

            await using (FileStream stream = new(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream).ConfigureAwait(false);
                
            }
            string fileContent = await File.ReadAllTextAsync(filePath);
            if (IsBase64String(fileContent))
            {
                byte[] binary = Convert.FromBase64String(fileContent);
                await File.WriteAllBytesAsync(filePath, binary);
            }

            totalBytes += file.Length;
        }

        _logger.LogInformation($"Total bytes received: {totalBytes}");
    }
    
    private static bool IsBase64String(string base64)
    {
        var buffer = new Span<byte>(new byte[base64.Length]);
        return Convert.TryFromBase64String(base64, buffer , out int _);
    }
    
    private static string ReplaceFirstOccurrence(string source, string oldValue, string newValue)
    {
        int pos = source.IndexOf(oldValue);
        if (pos < 0)
        {
            return source;
        }
        return source.Substring(0, pos) + newValue + source.Substring(pos + oldValue.Length);
    }

    [GeneratedRegex(@"!\[\[.*?\]\]")]
    private static partial Regex FileAttachmentRegex();
}