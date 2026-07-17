using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace LeXtudio.Metadata.Mutable.Tests
{
    public class MutableAssemblyDefinitionReadPackageTests
    {
        [Theory]
        [InlineData("Newtonsoft.Json", "13.0.3")]
        [InlineData("System.Text.Json", "9.0.0")]
        [InlineData("Microsoft.Extensions.DependencyInjection", "9.0.0")]
        [InlineData("Revit_All_Main_Versions_API_x64", "2027.0.2")]
        public async Task ReadAssemblyInPackage(string packageId, string version)
        {
            using var helper = new NuGetPackageHelper();

            var assemblies = await helper.DownloadPackageLibrariesAsync(
                packageId,
                version);

            Assert.NotEmpty(assemblies);

            foreach (var assembly in assemblies)
            {
                System.Console.WriteLine(assembly);
                var definition = MutableAssemblyDefinition.ReadAssembly(assembly);
                System.Console.WriteLine(definition?.FullName);
            }
        }
    }
}
