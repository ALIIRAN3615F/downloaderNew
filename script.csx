#!/usr/bin/env dotnet-script
#r "nuget: System.Text.Json, 8.0.0"
#r "nuget: System.Net.Http, 4.3.4"

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// کلاس کمکی برای دسترسی آسان به دیکشنری
public static class DictionaryExtensions
{
    public static TValue GetValueOrDefault<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, TValue defaultValue = default)
    {
        return dict.TryGetValue(key, out TValue value) ? value : defaultValue;
    }
}

// کلاس نگهداری اطلاعات پست
public class Post
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string Type { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Duration { get; set; }
    public long FileSize { get; set; }
    public string Extension { get; set; }
    public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
}

// کلاس پیکربندی سایت
public class SiteConfiguration
{
    public string BaseUrl { get; set; }
    public string SearchEndpoint { get; set; }
    public Dictionary<string, string> DefaultParameters { get; set; } = new Dictionary<string, string>();
    public int RateLimitDelay { get; set; } = 1000; // میلی‌ثانیه
    public bool RequiresAuth { get; set; } = false;
    public string AuthHeader { get; set; }
    public string AuthToken { get; set; }
    public string ResponseFormat { get; set; } = "json"; // json, xml, html
}

// کلاس مدیریت پیکربندی سایت‌ها
public class SiteConfigurator
{
    private static readonly Dictionary<string, SiteConfiguration> _configurations = new Dictionary<string, SiteConfiguration>
    {
        ["RX"] = new SiteConfiguration
        {
            BaseUrl = "https://api.rx-site.com",
            SearchEndpoint = "/v1/search",
            DefaultParameters = new Dictionary<string, string>
            {
                ["limit"] = "100",
                ["sort"] = "newest",
                ["rating"] = "safe"
            },
            RateLimitDelay = 1000,
            RequiresAuth = false,
            ResponseFormat = "json"
        },
        ["RP"] = new SiteConfiguration
        {
            BaseUrl = "https://api.rp-site.com",
            SearchEndpoint = "/api/v2/posts",
            DefaultParameters = new Dictionary<string, string>
            {
                ["limit"] = "50",
                ["order"] = "desc"
            },
            RateLimitDelay = 1500,
            RequiresAuth = false,
            ResponseFormat = "json"
        },
        ["RN"] = new SiteConfiguration
        {
            BaseUrl = "https://rn-site.net",
            SearchEndpoint = "/search",
            DefaultParameters = new Dictionary<string, string>
            {
                ["page"] = "1",
                ["per_page"] = "30"
            },
            RateLimitDelay = 2000,
            RequiresAuth = false,
            ResponseFormat = "html"
        },
        ["BB"] = new SiteConfiguration
        {
            BaseUrl = "https://bb-api.org",
            SearchEndpoint = "/posts",
            DefaultParameters = new Dictionary<string, string>
            {
                ["limit"] = "40",
                ["sort"] = "score"
            },
            RateLimitDelay = 1200,
            RequiresAuth = false,
            ResponseFormat = "json"
        },
        ["EN"] = new SiteConfiguration
        {
            BaseUrl = "https://en-site.com",
            SearchEndpoint = "/api/search",
            DefaultParameters = new Dictionary<string, string>
            {
                ["count"] = "25",
                ["mode"] = "extended"
            },
            RateLimitDelay = 800,
            RequiresAuth = false,
            ResponseFormat = "json"
        },
        ["RS"] = new SiteConfiguration
        {
            BaseUrl = "https://rs-site.io",
            SearchEndpoint = "/v3/search",
            DefaultParameters = new Dictionary<string, string>
            {
                ["limit"] = "60",
                ["order"] = "random"
            },
            RateLimitDelay = 1000,
            RequiresAuth = false,
            ResponseFormat = "json"
        }
    };

    public static SiteConfiguration GetConfiguration(string siteCode)
    {
        if (_configurations.TryGetValue(siteCode.ToUpper(), out var config))
        {
            return config;
        }
        
        // پیکربندی پیش‌فرض
        return new SiteConfiguration
        {
            BaseUrl = "https://api.default-site.com",
            SearchEndpoint = "/search",
            DefaultParameters = new Dictionary<string, string>
            {
                ["limit"] = "50",
                ["sort"] = "recent"
            },
            RateLimitDelay = 1000,
            RequiresAuth = false,
            ResponseFormat = "json"
        };
    }
}

// کلاس اصلی API Client
public class ApiClient
{
    protected readonly HttpClient _httpClient;
    protected readonly string _site;
    protected readonly SiteConfiguration _config;
    
    public ApiClient(string site)
    {
        _site = site.ToUpper();
        _config = SiteConfigurator.GetConfiguration(_site);
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(3);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        
        if (_config.RequiresAuth && !string.IsNullOrEmpty(_config.AuthHeader) && !string.IsNullOrEmpty(_config.AuthToken))
        {
            _httpClient.DefaultRequestHeaders.Add(_config.AuthHeader, _config.AuthToken);
        }
    }
    
    protected virtual async Task EnforceRateLimit()
    {
        await Task.Delay(_config.RateLimitDelay);
    }
    
    public virtual async Task<List<Post>> SearchPosts(string tags, int maxPosts)
    {
        var allPosts = new List<Post>();
        int page = 1;
        
        while (allPosts.Count < maxPosts)
        {
            var posts = await SearchPostsPage(tags, page);
            if (posts.Count == 0) break;
            
            allPosts.AddRange(posts);
            page++;
            
            if (allPosts.Count >= maxPosts) break;
            
            await EnforceRateLimit();
        }
        
        return allPosts.Take(maxPosts).ToList();
    }
    
    protected virtual async Task<List<Post>> SearchPostsPage(string tags, int page)
    {
        var parameters = new Dictionary<string, string>(_config.DefaultParameters)
        {
            ["tags"] = tags,
            ["page"] = page.ToString()
        };
        
        var queryString = string.Join("&", parameters.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_config.BaseUrl}{_config.SearchEndpoint}?{queryString}";
        
        try
        {
            var response = await _httpClient.GetStringAsync(url);
            return ParsePosts(response);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error fetching page {page}: {ex.Message}");
            return new List<Post>();
        }
    }
    
    protected virtual List<Post> ParsePosts(string response)
    {
        var posts = new List<Post>();
        
        try
        {
            if (_config.ResponseFormat == "html")
            {
                posts = ParseHtmlResponse(response);
            }
            else
            {
                posts = ParseJsonResponse(response);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing response: {ex.Message}");
        }
        
        return posts;
    }
    
    protected virtual List<Post> ParseJsonResponse(string json)
    {
        var posts = new List<Post>();
        
        try
        {
            // الگوهای مختلف برای پارس کردن JSON
            var patterns = new Dictionary<string, string>
            {
                // الگوی عمومی برای پست‌ها
                ["post_pattern"] = @"\{[^{}]*""(?:url|file_url|sample_url|preview_url|image_url|media_url)""\s*:\s*""([^""]+)""[^{}]*""(?:id|post_id)""\s*:\s*""?([^"",}]+)""?[^{}]*\}",
                
                // الگو برای اطلاعات ویدیو
                ["video_pattern"] = @"duration["":\s]+(\d+)",
                
                // الگو برای ابعاد
                ["dimension_pattern"] = @"(?:width["":\s]+(\d+).*?height["":\s]+(\d+)|height["":\s]+(\d+).*?width["":\s]+(\d+))",
                
                // الگو برای حجم فایل
                ["size_pattern"] = @"(?:file_size|size|length)["":\s]+(\d+)"
            };
            
            // جستجوی پست‌ها
            var postMatches = Regex.Matches(json, patterns["post_pattern"], RegexOptions.IgnoreCase | RegexOptions.Singleline);
            
            foreach (Match match in postMatches)
            {
                if (match.Groups.Count >= 3)
                {
                    var url = match.Groups[1].Value;
                    var id = match.Groups[2].Value;
                    
                    if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(id))
                    {
                        var post = new Post
                        {
                            Id = id,
                            Url = url,
                            Type = DetermineFileType(url),
                            Extension = Path.GetExtension(url)?.ToLower()
                        };
                        
                        // استخراج مدت زمان ویدیو
                        var durationMatch = Regex.Match(json.Substring(match.Index, Math.Min(500, json.Length - match.Index)), 
                                                      patterns["video_pattern"]);
                        if (durationMatch.Success && int.TryParse(durationMatch.Groups[1].Value, out int duration))
                        {
                            post.Duration = duration;
                        }
                        
                        // استخراج ابعاد
                        var dimensionMatch = Regex.Match(json.Substring(match.Index, Math.Min(500, json.Length - match.Index)), 
                                                        patterns["dimension_pattern"]);
                        if (dimensionMatch.Success)
                        {
                            if (dimensionMatch.Groups[1].Success && dimensionMatch.Groups[2].Success)
                            {
                                post.Width = int.Parse(dimensionMatch.Groups[1].Value);
                                post.Height = int.Parse(dimensionMatch.Groups[2].Value);
                            }
                            else if (dimensionMatch.Groups[3].Success && dimensionMatch.Groups[4].Success)
                            {
                                post.Height = int.Parse(dimensionMatch.Groups[3].Value);
                                post.Width = int.Parse(dimensionMatch.Groups[4].Value);
                            }
                        }
                        
                        // استخراج حجم فایل
                        var sizeMatch = Regex.Match(json.Substring(match.Index, Math.Min(500, json.Length - match.Index)), 
                                                   patterns["size_pattern"]);
                        if (sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out long fileSize))
                        {
                            post.FileSize = fileSize;
                        }
                        
                        posts.Add(post);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing JSON: {ex.Message}");
        }
        
        return posts;
    }
    
    protected virtual List<Post> ParseHtmlResponse(string html)
    {
        var posts = new List<Post>();
        
        try
        {
            // الگوهای مختلف برای پارس کردن HTML
            var patterns = new Dictionary<string, string>
            {
                // الگو برای لینک‌های تصاویر در HTML
                ["img_pattern"] = @"<img[^>]+src=[""']([^""']+\.(?:jpg|jpeg|png|gif|webp|bmp))[""'][^>]*>",
                
                // الگو برای لینک‌های ویدیو در HTML
                ["video_pattern"] = @"<(?:video|source)[^>]+src=[""']([^""']+\.(?:mp4|webm|avi|mov|mkv))[""'][^>]*>",
                
                // الگو برای لینک‌های عمومی
                ["link_pattern"] = @"<a[^>]+href=[""']([^""']+\.(?:jpg|jpeg|png|gif|webp|bmp|mp4|webm|avi|mov|mkv))[""'][^>]*>",
                
                // الگو برای data-src
                ["data_src_pattern"] = @"data-src=[""']([^""']+)[""']",
                
                // الگو برای data-url
                ["data_url_pattern"] = @"data-url=[""']([^""']+)[""']"
            };
            
            // جستجوی تمام لینک‌های رسانه
            var allUrls = new HashSet<string>();
            
            foreach (var pattern in patterns.Values)
            {
                var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    if (match.Groups.Count >= 2)
                    {
                        var url = match.Groups[1].Value;
                        
                        // تبدیل لینک نسبی به مطلق
                        if (url.StartsWith("/"))
                        {
                            url = _config.BaseUrl + url;
                        }
                        else if (!url.StartsWith("http"))
                        {
                            url = _config.BaseUrl + "/" + url;
                        }
                        
                        if (!allUrls.Contains(url))
                        {
                            allUrls.Add(url);
                            
                            var post = new Post
                            {
                                Id = Guid.NewGuid().ToString("N").Substring(0, 8),
                                Url = url,
                                Type = DetermineFileType(url),
                                Extension = Path.GetExtension(url)?.ToLower()
                            };
                            
                            posts.Add(post);
                        }
                    }
                }
            }
            
            // اگر هیچ لینکی پیدا نشد، سعی می‌کنیم از data attributes استفاده کنیم
            if (posts.Count == 0)
            {
                // الگو برای div با data attributes
                var divPattern = @"<div[^>]+data-(?:id|post-id)=[""']([^""']+)[""'][^>]*data-(?:url|file-url)=[""']([^""']+)[""'][^>]*>";
                var divMatches = Regex.Matches(html, divPattern, RegexOptions.IgnoreCase);
                
                foreach (Match match in divMatches)
                {
                    if (match.Groups.Count >= 3)
                    {
                        var id = match.Groups[1].Value;
                        var url = match.Groups[2].Value;
                        
                        if (!string.IsNullOrEmpty(url))
                        {
                            // تبدیل لینک نسبی به مطلق
                            if (url.StartsWith("/"))
                            {
                                url = _config.BaseUrl + url;
                            }
                            else if (!url.StartsWith("http"))
                            {
                                url = _config.BaseUrl + "/" + url;
                            }
                            
                            var post = new Post
                            {
                                Id = id,
                                Url = url,
                                Type = DetermineFileType(url),
                                Extension = Path.GetExtension(url)?.ToLower()
                            };
                            
                            posts.Add(post);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing HTML: {ex.Message}");
        }
        
        return posts;
    }
    
    protected virtual string DetermineFileType(string url)
    {
        var extension = Path.GetExtension(url)?.ToLower();
        
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" or ".tiff" => "image",
            ".gif" => "gif",
            ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv" or ".flv" or ".wmv" => "video",
            _ => "unknown"
        };
    }
}

// کلاس پیشرفته API Client
public class AdvancedApiClient : ApiClient
{
    public AdvancedApiClient(string site) : base(site) { }
    
    protected override async Task EnforceRateLimit()
    {
        Console.WriteLine($"Rate limiting: Waiting {_config.RateLimitDelay}ms for {_site}");
        await base.EnforceRateLimit();
    }
    
    protected override async Task<List<Post>> SearchPostsPage(string tags, int page)
    {
        Console.WriteLine($"Fetching page {page} from {_site} with tags: {tags}");
        return await base.SearchPostsPage(tags, page);
    }
    
    protected override List<Post> ParsePosts(string response)
    {
        Console.WriteLine($"Parsing {_config.ResponseFormat.ToUpper()} response from {_site}");
        var posts = base.ParsePosts(response);
        Console.WriteLine($"Found {posts.Count} posts in response");
        return posts;
    }
}

// کلاس مدیریت دانلود
public class DownloadManager
{
    private readonly HttpClient _httpClient;
    private readonly string _downloadFolder;
    private readonly bool _compressFiles;
    private readonly long _compressionThreshold;
    private readonly long _partitionThreshold;
    
    public DownloadManager(string downloadFolder, bool compressFiles, long compressionThreshold, long partitionThreshold)
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _downloadFolder = downloadFolder;
        _compressFiles = compressFiles;
        _compressionThreshold = compressionThreshold;
        _partitionThreshold = partitionThreshold;
        
        Directory.CreateDirectory(downloadFolder);
    }
    
    public async Task<string> DownloadFileAsync(string url, string subFolder, string fileName = null)
    {
        try
        {
            string folderPath = Path.Combine(_downloadFolder, subFolder);
            Directory.CreateDirectory(folderPath);
            
            if (string.IsNullOrEmpty(fileName))
            {
                fileName = GenerateFileName(url);
            }
            
            string filePath = Path.Combine(folderPath, fileName);
            
            if (File.Exists(filePath))
            {
                Console.WriteLine($"File already exists: {filePath}");
                return filePath;
            }
            
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            
            using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await response.Content.CopyToAsync(fileStream);
            
            var fileInfo = new FileInfo(filePath);
            Console.WriteLine($"Downloaded: {fileName} ({FormatFileSize(fileInfo.Length)}) to {subFolder}/");
            
            if (_compressFiles && fileInfo.Length > _compressionThreshold)
            {
                CompressFile(filePath);
            }
            
            if (fileInfo.Length > _partitionThreshold)
            {
                Console.WriteLine($"Warning: {fileName} exceeds partition threshold ({FormatFileSize(_partitionThreshold)})");
            }
            
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download {url}: {ex.Message}");
            return null;
        }
    }
    
    private string GenerateFileName(string url)
    {
        string baseName = Path.GetFileName(url.Split('?')[0]);
        string extension = Path.GetExtension(baseName);
        string nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
        
        // حذف کاراکترهای نامعتبر
        nameWithoutExt = Regex.Replace(nameWithoutExt, @"[^\w\-]", "_");
        
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string random = Guid.NewGuid().ToString("N").Substring(0, 8);
        
        return $"{nameWithoutExt}_{timestamp}_{random}{extension}";
    }
    
    private void CompressFile(string filePath)
    {
        try
        {
            string compressedPath = filePath + ".gz";
            
            using var originalStream = File.OpenRead(filePath);
            using var compressedStream = File.Create(compressedPath);
            using var gzipStream = new GZipStream(compressedStream, CompressionLevel.Optimal);
            
            originalStream.CopyTo(gzipStream);
            
            var originalSize = new FileInfo(filePath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            var compressionRatio = (1 - (double)compressedSize / originalSize) * 100;
            
            File.Delete(filePath);
            
            Console.WriteLine($"Compressed: {Path.GetFileName(filePath)} " +
                             $"{FormatFileSize(originalSize)} -> {FormatFileSize(compressedSize)} " +
                             $"(ratio: {compressionRatio:F1}%)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Compression failed for {filePath}: {ex.Message}");
        }
    }
    
    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0;
        double len = bytes;
        
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        
        return $"{len:0.##} {sizes[order]}";
    }
}

// کلاس زمان‌بندی محتوا
public class ContentScheduler
{
    private readonly Dictionary<string, int> _targets;
    private readonly Dictionary<string, int> _counters;
    private readonly int _videoMinDuration;
    
    public ContentScheduler(int images, int gifs, int videos, int videoMinDuration)
    {
        _targets = new Dictionary<string, int>
        {
            ["image"] = images,
            ["gif"] = gifs,
            ["video"] = videos
        };
        
        _counters = new Dictionary<string, int>
        {
            ["image"] = 0,
            ["gif"] = 0,
            ["video"] = 0
        };
        
        _videoMinDuration = videoMinDuration;
    }
    
    public bool NeedsMore(string type)
    {
        return _counters[type] < _targets[type];
    }
    
    public bool CanAcceptPost(Post post)
    {
        if (!_counters.ContainsKey(post.Type))
            return false;
        
        if (!NeedsMore(post.Type))
            return false;
        
        if (post.Type == "video" && post.Duration < _videoMinDuration)
            return false;
        
        return true;
    }
    
    public void IncrementCounter(string type)
    {
        if (_counters.ContainsKey(type))
        {
            _counters[type]++;
        }
    }
    
    public bool IsComplete()
    {
        return _counters["image"] >= _targets["image"] &&
               _counters["gif"] >= _targets["gif"] &&
               _counters["video"] >= _targets["video"];
    }
    
    public void PrintStatus()
    {
        Console.WriteLine($"Progress: Images {_counters["image"]}/{_targets["image"]}, " +
                         $"GIFs {_counters["gif"]}/{_targets["gif"]}, " +
                         $"Videos {_counters["video"]}/{_targets["video"]}");
    }
}

// برنامه اصلی
public class Program
{
    public static async Task Main(string[] args)
    {
        var arguments = ParseArguments(args);
        
        string tags = arguments.GetValueOrDefault("tags", "catGirl");
        int images = int.Parse(arguments.GetValueOrDefault("images", "5"));
        int gifs = int.Parse(arguments.GetValueOrDefault("gifs", "3"));
        int videos = int.Parse(arguments.GetValueOrDefault("videos", "10"));
        int videoMinDuration = int.Parse(arguments.GetValueOrDefault("video-min-duration", "0"));
        int maxTotal = int.Parse(arguments.GetValueOrDefault("max-total", "3000"));
        string site = arguments.GetValueOrDefault("site", "RX");
        bool compress = !arguments.ContainsKey("no-compress");
        
        const long compressionThreshold = 90 * 1024 * 1024;
        const long partitionThreshold = 90 * 1024 * 1024;
        
        Console.WriteLine($"=== Content Downloader ===");
        Console.WriteLine($"Site: {site}");
        Console.WriteLine($"Tags: {tags}");
        Console.WriteLine($"Targets: {images} images, {gifs} GIFs, {videos} videos (min {videoMinDuration}s)");
        Console.WriteLine($"Max posts to check: {maxTotal}");
        Console.WriteLine($"Compression threshold: {compressionThreshold / (1024 * 1024)}MB");
        Console.WriteLine($"Partition threshold: {partitionThreshold / (1024 * 1024)}MB");
        Console.WriteLine($"Compression enabled: {compress}");
        Console.WriteLine($"Start time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine(new string('=', 50));
        
        var scheduler = new ContentScheduler(images, gifs, videos, videoMinDuration);
        var downloadManager = new DownloadManager("downloads", compress, compressionThreshold, partitionThreshold);
        var apiClient = new AdvancedApiClient(site);
        
        var posts = await apiClient.SearchPosts(tags, maxTotal);
        Console.WriteLine($"Found {posts.Count} posts from API");
        
        var downloadedFiles = new List<string>();
        int processedCount = 0;
        
        foreach (var post in posts)
        {
            if (scheduler.IsComplete())
                break;
            
            if (!scheduler.CanAcceptPost(post))
                continue;
            
            string subFolder = post.Type switch
            {
                "image" => "images",
                "gif" => "gifs",
                "video" => "videos",
                _ => "other"
            };
            
            var filePath = await downloadManager.DownloadFileAsync(post.Url, subFolder);
            
            if (filePath != null)
            {
                downloadedFiles.Add(filePath);
                scheduler.IncrementCounter(post.Type);
                scheduler.PrintStatus();
            }
            
            processedCount++;
            
            if (processedCount % 10 == 0)
            {
                Console.WriteLine($"Processed {processedCount}/{posts.Count} posts");
            }
            
            await Task.Delay(100);
        }
        
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"Download completed!");
        Console.WriteLine($"Total processed: {processedCount} posts");
        Console.WriteLine($"Successfully downloaded: {downloadedFiles.Count} files");
        Console.WriteLine($"Final status:");
        scheduler.PrintStatus();
        
        var largeFiles = downloadedFiles.Where(f => new FileInfo(f).Length > partitionThreshold).ToList();
        if (largeFiles.Count > 0)
        {
            Console.WriteLine($"\nLarge files (> {partitionThreshold / (1024 * 1024)}MB):");
            foreach (var file in largeFiles)
            {
                var info = new FileInfo(file);
                Console.WriteLine($"  {Path.GetFileName(file)}: {info.Length / (1024 * 1024)}MB");
            }
        }
        
        Console.WriteLine($"\nEnd time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Total execution time: {DateTime.Now - DateTime.Now.AddSeconds(-processedCount * 0.1):hh\\:mm\\:ss}");
    }
    
    static Dictionary<string, string> ParseArguments(string[] args)
    {
        var result = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "true";
                }
            }
        }
        return result;
    }
}

// کد اصلی اجرایی
var arguments = ParseArguments(Args.ToArray());

string tags = arguments.GetValueOrDefault("tags", "catGirl");
int images = int.Parse(arguments.GetValueOrDefault("images", "5"));
int gifs = int.Parse(arguments.GetValueOrDefault("gifs", "3"));
int videos = int.Parse(arguments.GetValueOrDefault("videos", "10"));
int videoMinDuration = int.Parse(arguments.GetValueOrDefault("video-min-duration", "0"));
int maxTotal = int.Parse(arguments.GetValueOrDefault("max-total", "3000"));
string site = arguments.GetValueOrDefault("site", "RX");
bool compress = !arguments.ContainsKey("no-compress");

const long compressionThreshold = 90 * 1024 * 1024;
const long partitionThreshold = 90 * 1024 * 1024;

Console.WriteLine($"=== Content Downloader ===");
Console.WriteLine($"Site: {site}");
Console.WriteLine($"Tags: {tags}");
Console.WriteLine($"Targets: {images} images, {gifs} GIFs, {videos} videos (min {videoMinDuration}s)");
Console.WriteLine($"Max posts to check: {maxTotal}");
Console.WriteLine($"Compression threshold: {compressionThreshold / (1024 * 1024)}MB");
Console.WriteLine($"Partition threshold: {partitionThreshold / (1024 * 1024)}MB");
Console.WriteLine($"Compression enabled: {compress}");
Console.WriteLine($"Start time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine(new string('=', 50));

var scheduler = new ContentScheduler(images, gifs, videos, videoMinDuration);
var downloadManager = new DownloadManager("downloads", compress, compressionThreshold, partitionThreshold);
var apiClient = new AdvancedApiClient(site);

var posts = await apiClient.SearchPosts(tags, maxTotal);
Console.WriteLine($"Found {posts.Count} posts from API");

var downloadedFiles = new List<string>();
int processedCount = 0;

foreach (var post in posts)
{
    if (scheduler.IsComplete())
        break;
    
    if (!scheduler.CanAcceptPost(post))
        continue;
    
    string subFolder = post.Type switch
    {
        "image" => "images",
        "gif" => "gifs",
        "video" => "videos",
        _ => "other"
    };
    
    var filePath = await downloadManager.DownloadFileAsync(post.Url, subFolder);
    
    if (filePath != null)
    {
        downloadedFiles.Add(filePath);
        scheduler.IncrementCounter(post.Type);
        scheduler.PrintStatus();
    }
    
    processedCount++;
    
    if (processedCount % 10 == 0)
    {
        Console.WriteLine($"Processed {processedCount}/{posts.Count} posts");
    }
    
    await Task.Delay(100);
}

Console.WriteLine(new string('=', 50));
Console.WriteLine($"Download completed!");
Console.WriteLine($"Total processed: {processedCount} posts");
Console.WriteLine($"Successfully downloaded: {downloadedFiles.Count} files");
Console.WriteLine($"Final status:");
scheduler.PrintStatus();

var largeFiles = downloadedFiles.Where(f => new FileInfo(f).Length > partitionThreshold).ToList();
if (largeFiles.Count > 0)
{
    Console.WriteLine($"\nLarge files (> {partitionThreshold / (1024 * 1024)}MB):");
    foreach (var file in largeFiles)
    {
        var info = new FileInfo(file);
        Console.WriteLine($"  {Path.GetFileName(file)}: {info.Length / (1024 * 1024)}MB");
    }
}

Console.WriteLine($"\nEnd time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
Console.WriteLine($"Total execution time: {DateTime.Now - DateTime.Now.AddSeconds(-processedCount * 0.1):hh\\:mm\\:ss}");

// تابع ParseArguments برای کد بالا
Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--"))
        {
            string key = args[i].Substring(2);
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                result[key] = args[i + 1];
                i++;
            }
            else
            {
                result[key] = "true";
            }
        }
    }
    return result;
}
