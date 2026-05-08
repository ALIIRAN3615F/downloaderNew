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
    public int RateLimitDelay { get; set; } = 1000;
    public bool RequiresAuth { get; set; } = false;
    public string AuthHeader { get; set; }
    public string AuthToken { get; set; }
    public string ResponseFormat { get; set; } = "json";
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
        }
    };

    public static SiteConfiguration GetConfiguration(string siteCode)
    {
        if (_configurations.TryGetValue(siteCode.ToUpper(), out var config))
        {
            return config;
        }
        
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
        return ParseJsonResponse(response);
    }
    
    protected virtual List<Post> ParseJsonResponse(string json)
    {
        var posts = new List<Post>();
        
        try
        {
            // الگوی ساده برای پیدا کردن URLها
            var urlPattern = @"""url""\s*:\s*""([^""]+)""";
            var urlMatches = Regex.Matches(json, urlPattern, RegexOptions.IgnoreCase);
            
            foreach (Match match in urlMatches)
            {
                if (match.Groups.Count >= 2)
                {
                    var url = match.Groups[1].Value;
                    
                    if (!string.IsNullOrEmpty(url) && 
                        (url.Contains(".jpg") || url.Contains(".png") || url.Contains(".gif") || 
                         url.Contains(".mp4") || url.Contains(".webm")))
                    {
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
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing JSON: {ex.Message}");
        }
        
        return posts;
    }
    
    protected virtual string DetermineFileType(string url)
    {
        var extension = Path.GetExtension(url)?.ToLower();
        
        return extension switch
        {
            ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp" => "image",
            ".gif" => "gif",
            ".mp4" or ".webm" or ".avi" or ".mov" or ".mkv" => "video",
            _ => "unknown"
        };
    }
}

// کلاس مدیریت دانلود
public class DownloadManager
{
    private readonly HttpClient _httpClient;
    private readonly string _downloadFolder;
    
    public DownloadManager(string downloadFolder)
    {
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(5);
        _downloadFolder = downloadFolder;
        
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
            Console.WriteLine($"Downloaded: {fileName} ({fileInfo.Length / 1024} KB) to {subFolder}/");
            
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
        
        nameWithoutExt = Regex.Replace(nameWithoutExt, @"[^\w\-]", "_");
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string random = Guid.NewGuid().ToString("N").Substring(0, 8);
        
        return $"{nameWithoutExt}_{timestamp}_{random}{extension}";
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

// تابع پارس آرگومان‌ها
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

// کد اصلی اجرایی
try
{
    var arguments = ParseArguments(Args.ToArray());
    
    string tags = arguments.GetValueOrDefault("tags", "catGirl");
    int images = int.Parse(arguments.GetValueOrDefault("images", "0"));
    int gifs = int.Parse(arguments.GetValueOrDefault("gifs", "0"));
    int videos = int.Parse(arguments.GetValueOrDefault("videos", "10"));
    int videoMinDuration = int.Parse(arguments.GetValueOrDefault("video-min-duration", "0"));
    int maxTotal = int.Parse(arguments.GetValueOrDefault("max-total", "3000"));
    string site = arguments.GetValueOrDefault("site", "RX");
    
    Console.WriteLine($"=== Content Downloader ===");
    Console.WriteLine($"Site: {site}");
    Console.WriteLine($"Tags: {tags}");
    Console.WriteLine($"Targets: {images} images, {gifs} GIFs, {videos} videos (min {videoMinDuration}s)");
    Console.WriteLine($"Max posts to check: {maxTotal}");
    Console.WriteLine($"Start time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine(new string('=', 50));
    
    var scheduler = new ContentScheduler(images, gifs, videos, videoMinDuration);
    var downloadManager = new DownloadManager("downloads");
    var apiClient = new ApiClient(site);
    
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
    Console.WriteLine($"\nEnd time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack trace: {ex.StackTrace}");
    Environment.Exit(1);
}
