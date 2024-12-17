using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

class Program
{
    private static readonly string RootDir = Path.Combine(Directory.GetCurrentDirectory(), "img");
    private static readonly HttpClient client = new HttpClient();
    private static readonly int MaxConcurrentDownloads = 50;

    static async Task Main(string[] args)
    {
        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        Console.WriteLine(@"
•-------------------•
| ParseR x rule34   |
•-------------------•

");

        Console.Write("Enter the tag: ");
        string tag = Console.ReadLine().Trim();

        int totalPosts = await GetTotalPosts(tag);
        int postsPerPage = 42;
        int totalPages = (totalPosts + postsPerPage - 1) / postsPerPage;

        Console.WriteLine($"Total number of pages for the tag '{tag}': {totalPages}");

        Console.Write($"Enter the number of pages to download from (max {totalPages}): ");
        int maxPages = int.Parse(Console.ReadLine().Trim());
        if (maxPages > totalPages)
        {
            Console.WriteLine($"Maximum number of pages is {totalPages}. Setting max_pages to {totalPages}.");
            maxPages = totalPages;
        }

        Console.Clear();
        Console.WriteLine(@"
•-------------------•
| ParseR x rule34   |
•-------------------•

Loading URLs...
");

        List<string[]> allUrls = new List<string[]>();
        for (int page = 0; page < maxPages; page++)
        {
            Console.WriteLine($"Processing page {page + 1}/{maxPages}");
            var listUrl = await ListTag(page, tag);
            allUrls.AddRange(listUrl);
        }

        string tagDir = GetUniqueFolder(RootDir, tag);

        Console.Clear();
        Console.WriteLine(@"
•-------------------•
| ParseR x rule34   |
•-------------------•

Downloading files...
");

        await DownloadFiles(allUrls, tagDir);

        stopwatch.Stop();

        Console.Clear();
        Console.WriteLine(@"
•-------------------•
| ParseR x rule34   |
•-------------------•

");
        Console.WriteLine($"{allUrls.Count} files downloaded to {tagDir}");
        Console.WriteLine($"Total time taken: {stopwatch.Elapsed}");


        Console.WriteLine("Press any key to exit...");
        Console.ReadKey();
    }

    private static async Task<int> GetTotalPosts(string tag)
    {
        string tags = Uri.EscapeDataString(tag);
        string url = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&limit=1&tags={tags}";
        HttpResponseMessage response = await SendRequestWithRetry(url);
        string responseContent = await response.Content.ReadAsStringAsync();
        XDocument xmlDoc = XDocument.Parse(responseContent);
        if (xmlDoc.Root?.Attribute("count") != null)
        {
            return int.Parse(xmlDoc.Root.Attribute("count").Value);
        }
        else
        {
            throw new Exception("Could not find 'count' attribute in the response.");
        }
    }

    private static async Task<List<string[]>> ListTag(int page, string tag)
    {
        string tags = Uri.EscapeDataString(tag);
        string url = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&pid={page}&tags={tags}";
        HttpResponseMessage response = await SendRequestWithRetry(url);
        string responseContent = await response.Content.ReadAsStringAsync();
        XDocument xmlDoc = XDocument.Parse(responseContent);
        List<string[]> returnData = new List<string[]>();

        foreach (var post in xmlDoc.Descendants("post"))
        {
            XAttribute fileUrlAttr = post.Attribute("file_url");
            if (fileUrlAttr != null)
            {
                string fileUrl = fileUrlAttr.Value;
                if (fileUrl.ToLower().EndsWith(".jpg"))
                {
                    XAttribute tagsAttr = post.Attribute("tags");
                    string tagsValue = tagsAttr?.Value ?? "";
                    returnData.Add(new string[] { tagsValue, fileUrl });
                }
            }
        }

        return returnData;
    }

    private static string GetUniqueFolder(string baseDir, string tag)
    {
        string basePath = Path.Combine(baseDir, tag);
        int counter = 0;
        while (true)
        {
            string path;
            if (counter == 0)
            {
                path = basePath;
            }
            else
            {
                path = $"{basePath}({counter})";
            }
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                return path;
            }
            counter++;
        }
    }

    private static async Task DownloadFiles(List<string[]> urls, string tagDir)
    {
        List<Task> downloadTasks = new List<Task>();
        SemaphoreSlim semaphore = new SemaphoreSlim(MaxConcurrentDownloads);

        foreach (var oneUrl in urls)
        {
            await semaphore.WaitAsync();
            downloadTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await LoadUrl(oneUrl[1], tagDir);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(downloadTasks);
    }

    private static async Task LoadUrl(string url, string tagDir)
    {
        try
        {
            string filename = Path.Combine(tagDir, Path.GetFileName(url));
            if (File.Exists(filename))
            {
                Console.WriteLine($"File {filename} already exists. Skipping.");
                return;
            }

            HttpResponseMessage response = await SendRequestWithRetry(url);
            response.EnsureSuccessStatusCode();
            byte[] content = await response.Content.ReadAsByteArrayAsync();
            await File.WriteAllBytesAsync(filename, content);
            Console.WriteLine($"Downloaded {filename}");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"HTTP Error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    private static async Task<HttpResponseMessage> SendRequestWithRetry(string url)
    {
        int retryCount = 0;
        while (retryCount < 5) // Maximum number of retries for 429 errors
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return response;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                retryCount++;
                int delay = 1000 * (int)Math.Pow(2, retryCount - 1);
                delay += Random.Shared.Next(0, 500); 
                Console.WriteLine($"429 Too Many Requests. Retrying in {delay} ms...");
                await Task.Delay(delay);
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"HTTP Error: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
        throw new HttpRequestException("Exceeded maximum number of retries for 429 Too Many Requests.");
    }
}
