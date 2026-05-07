#!/usr/bin/env dotnet-script

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Linq;

// ===================== تنظیمات =====================
int wantedImages = 0;
int wantedGifs   = 0;
int wantedVideos = 0;
string tags     = null;
string outputDir = "downloads";
int maxTotalPosts = 2000;   // سقف جستجو برای جلوگیری از حلقه بی‌نهایت

// ===================== پارس آرگومان‌ها =====================
var args = Args.ToArray();
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--tags" && i + 1 < args.Length)
        tags = args[++i];
    else if (args[i] == "--images" && i + 1 < args.Length)
        wantedImages = int.Parse(args[++i]);
    else if (args[i] == "--gifs" && i + 1 < args.Length)
        wantedGifs = int.Parse(args[++i]);
    else if (args[i] == "--videos" && i + 1 < args.Length)
        wantedVideos = int.Parse(args[++i]);
    else if (args[i] == "--path" && i + 1 < args.Length)
        outputDir = args[++i];
    else if (args[i] == "--max-total" && i + 1 < args.Length)
        maxTotalPosts = int.Parse(args[++i]);
}

// بررسی حداقل نیاز
if (string.IsNullOrWhiteSpace(tags) || (wantedImages + wantedGifs + wantedVideos == 0))
{
    Console.Error.WriteLine("Usage: dotnet script script.csx --tags <tags> [--images <n>] [--gifs <n>] [--videos <n>] [--path <dir>] [--max-total <n>]");
    Console.Error.WriteLine("Example: dotnet script script.csx --tags 'angel' --images 10 --gifs 10 --videos 10");
    return 1;
}

Console.WriteLine($"🚀 شروع دانلود: tags='{tags}' | images={wantedImages}, gifs={wantedGifs}, videos={wantedVideos}");
Console.WriteLine($"📁 پوشه خروجی: {Path.GetFullPath(outputDir)}");

// ===================== سرویس دانلود =====================
async Task DownloadFileAsync(string url, string savePath)
{
    if (File.Exists(savePath))
    {
        Console.WriteLine($"  ⏭ فایل وجود دارد: {savePath}");
        return;
    }

    var dir = Path.GetDirectoryName(savePath);
    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        Directory.CreateDirectory(dir);

    var handler = new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer()
    };
    handler.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
    handler.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");

    for (int retry = 0; retry < 3; retry++)
    {
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            using var fileStream = File.Create(savePath);
            await stream.CopyToAsync(fileStream);
            Console.WriteLine($"  ✅ دانلود شد: {savePath} ({(int)response.Content.Headers.ContentLength} bytes)");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️ تلاش {retry+1} ناموفق: {ex.Message}");
            await Task.Delay(2000);
        }
    }
    Console.WriteLine($"  ❌ دانلود نهایی ناموفق: {url}");
}

// ===================== سرویس API =====================
// یک صفحه از پست‌ها رو برمیگردونه
async Task<List<PostInfo>> FetchPostsPageAsync(string tags, int pid)
{
    string encodedTags = Uri.EscapeDataString(tags).Replace("%20", "+");
    string apiUrl = $"https://api.rule34.xxx/index.php?page=dapi&s=post&q=index&tags={encodedTags}&pid={pid}&limit=100";

    Console.WriteLine($"📡 درخواست API: {apiUrl}");

    var handler = new HttpClientHandler
    {
        UseCookies = true,
        CookieContainer = new CookieContainer()
    };
    handler.CookieContainer.Add(new Cookie("gdpr", "1", "/", "rule34.xxx"));
    handler.CookieContainer.Add(new Cookie("gdpr-consent", "1", "/", "rule34.xxx"));

    using var client = new HttpClient(handler);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Referrer = new Uri("https://rule34.xxx/");

    string xmlContent;
    try
    {
        var response = await client.GetAsync(apiUrl);
        response.EnsureSuccessStatusCode();
        xmlContent = await response.Content.ReadAsStringAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ خطا در دریافت API: {ex.Message}");
        return new List<PostInfo>();
    }

    if (string.IsNullOrWhiteSpace(xmlContent))
    {
        Console.WriteLine("⚠️ پاسخ API خالی است.");
        return new List<PostInfo>();
    }

    XmlDocument doc = new XmlDocument();
    try
    {
        doc.LoadXml(xmlContent);
    }
    catch (XmlException ex)
    {
        Console.WriteLine($"❌ XML نامعتبر: {ex.Message}");
        Console.WriteLine($"محتوای دریافتی (۳۰۰ کاراکتر اول): {xmlContent.Substring(0, Math.Min(300, xmlContent.Length))}");
        return new List<PostInfo>();
    }

    if (doc.DocumentElement == null)
    {
        Console.WriteLine("⚠️ سند XML بدون ریشه است.");
        return new List<PostInfo>();
    }

    var posts = new List<PostInfo>();
    foreach (XmlNode node in doc.DocumentElement.ChildNodes)
    {
        if (node.Name != "post") continue;

        var idAttr = node.Attributes?["id"];
        var fileUrlAttr = node.Attributes?["file_url"];
        if (idAttr == null || fileUrlAttr == null) continue;

        string id = idAttr.Value;
        string fileUrl = fileUrlAttr.Value;
        string extension = Path.GetExtension(fileUrl)?.ToLowerInvariant() ?? "";

        posts.Add(new PostInfo
        {
            Id = id,
            FileUrl = fileUrl,
            Extension = extension
        });
    }

    Console.WriteLine($"✅ {posts.Count} پست از صفحه {pid} بازیابی شد.");
    return posts;
}

// ===================== ساختار داده =====================
class PostInfo
{
    public string Id { get; set; }
    public string FileUrl { get; set; }
    public string Extension { get; set; }
}

// ===================== حلقه اصلی =====================
int downloadedImages = 0;
int downloadedGifs   = 0;
int downloadedVideos = 0;
int processedPosts   = 0;
int pid = 0;   // شماره صفحه از صفر شروع می‌شود

while ((downloadedImages < wantedImages || downloadedGifs < wantedGifs || downloadedVideos < wantedVideos)
       && processedPosts < maxTotalPosts)
{
    var pagePosts = await FetchPostsPageAsync(tags, pid);
    if (pagePosts.Count == 0)
    {
        Console.WriteLine("🏁 هیچ پست بیشتری یافت نشد. جستجو پایان یافت.");
        break;
    }

    foreach (var post in pagePosts)
    {
        // اگر همه نیازها برآورده شده، از حلقه خارج شو
        if (downloadedImages >= wantedImages &&
            downloadedGifs >= wantedGifs &&
            downloadedVideos >= wantedVideos)
            break;

        string type = "";
        string subDir = "";

        if (post.Extension == ".mp4" || post.Extension == ".webm")
        {
            if (downloadedVideos < wantedVideos)
            {
                type = "video";
                subDir = "Video";
            }
        }
        else if (post.Extension == ".gif")
        {
            if (downloadedGifs < wantedGifs)
            {
                type = "gif";
                subDir = "Gif";
            }
        }
        else // تصاویر معمولی (jpg, png, jpeg, ...)
        {
            if (downloadedImages < wantedImages)
            {
                type = "image";
                subDir = "Images";
            }
        }

        if (!string.IsNullOrEmpty(type))
        {
            string fileName = $"{post.Id}{post.Extension}";
            string savePath = Path.Combine(outputDir, subDir, fileName);
            Console.WriteLine($"⬇ [{type}] {post.FileUrl}");
            await DownloadFileAsync(post.FileUrl, savePath);

            if (type == "image") downloadedImages++;
            else if (type == "gif") downloadedGifs++;
            else if (type == "video") downloadedVideos++;
        }

        processedPosts++;
        await Task.Delay(150); // احترام به سرور
    }

    pid++; // صفحه بعدی
    if (pid > 20) // حداکثر ۲۰ صفحه (۲۰۰۰ پست) با maxTotalPosts هم محدود می‌شه
    {
        Console.WriteLine("⚠️ تعداد صفحات بیش از حد مجاز شد.");
        break;
    }
}

Console.WriteLine($"\n🎉 پایان. تصاویر: {downloadedImages}/{wantedImages} | گیف: {downloadedGifs}/{wantedGifs} | ویدیو: {downloadedVideos}/{wantedVideos}");
