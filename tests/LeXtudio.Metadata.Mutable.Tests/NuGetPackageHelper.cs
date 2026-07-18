using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace LeXtudio.Metadata.Mutable.Tests
{
    public sealed class NuGetPackageHelper : IDisposable
    {
        private readonly string _rootDirectory;
        private readonly HttpClient _httpClient = new();

        public NuGetPackageHelper()
        {
            _rootDirectory = Path.Combine(
                Path.GetTempPath(),
                "NuGetTests",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_rootDirectory);
        }

        public async Task<IReadOnlyList<string>> DownloadPackageLibrariesAsync(
            string packageId,
            string version)
        {
            var packageFile = Path.Combine(_rootDirectory, $"{packageId}.{version}.nupkg");

            var url =
                $"https://www.nuget.org/api/v2/package/{packageId}/{version}";

            using (var response = await _httpClient.GetAsync(url))
            {
                response.EnsureSuccessStatusCode();

                await using var stream = File.Create(packageFile);
                await response.Content.CopyToAsync(stream);
            }

            var extractDirectory = Path.Combine(_rootDirectory, "package");

            ZipFile.ExtractToDirectory(packageFile, extractDirectory);

            var libDirectory = Directory
                .EnumerateDirectories(extractDirectory, "lib", SearchOption.AllDirectories)
                .FirstOrDefault();

            if (libDirectory is null)
                return Array.Empty<string>();

            return Directory
                .EnumerateFiles(libDirectory, "*.dll", SearchOption.AllDirectories)
                .ToList();
        }

        public void Dispose()
        {
            _httpClient.Dispose();

            try
            {
                if (Directory.Exists(_rootDirectory))
                    Directory.Delete(_rootDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures.
            }
        }
    }
}