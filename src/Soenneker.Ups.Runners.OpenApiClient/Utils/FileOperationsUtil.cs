using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Ups.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.OpenApi.Merger.Abstract;
using System.Collections.Generic;
using System.Text;
using Soenneker.Utils.Yaml.Abstract;

namespace Soenneker.Ups.Runners.OpenApiClient.Utils;

///<inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IProcessUtil _processUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IOpenApiMerger _openApiMerger;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IYamlUtil _yamlUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IProcessUtil processUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IOpenApiMerger openApiMerger, IOpenApiFixer openApiFixer,
        IYamlUtil yamlUtil, IKiotaUtil kiotaUtil)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _processUtil = processUtil;
        _kiotaUtil = kiotaUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
        _openApiMerger = openApiMerger;
        _openApiFixer = openApiFixer;
        _yamlUtil = yamlUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}",
            cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiGitUrl = _configuration["Ups:ClientGenerationUrl"] ?? "https://github.com/UPS-API/api-documentation";

        string sourceDirectory = await _gitUtil.CloneToTempDirectory(openApiGitUrl, cancellationToken: cancellationToken);
        string jsonDirectory = await ConvertAllOpenApiFilesToJson(sourceDirectory, cancellationToken);

        var merged = await _openApiMerger.MergeDirectory(jsonDirectory, cancellationToken);

        string json = _openApiMerger.ToJson(merged);

        await _fileUtil.Write(targetFilePath, json, true, cancellationToken);

        string fixedFilePath = Path.Combine(gitDirectory, "fixed.json");

        await _openApiFixer.Fix(targetFilePath, fixedFilePath, cancellationToken);

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "UpsOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken)
            .NoSync();
    }

    private async ValueTask<string> ConvertAllOpenApiFilesToJson(string sourceDirectory, CancellationToken cancellationToken)
    {
        string jsonDirectory = Path.Combine(Path.GetTempPath(), $"ups-openapi-json-{Guid.NewGuid():N}");

        await _directoryUtil.Create(jsonDirectory, false, cancellationToken);

        string[] filePaths = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories)
                                      .Where(IsSupportedOpenApiFile)
                                      .ToArray();

        foreach (string filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            string targetJsonPath = Path.Combine(jsonDirectory, Path.ChangeExtension(relativePath, ".json"));
            string? targetJsonDirectory = Path.GetDirectoryName(targetJsonPath);

            if (!string.IsNullOrWhiteSpace(targetJsonDirectory))
                await _directoryUtil.Create(targetJsonDirectory, false, cancellationToken);

            var yaml = await _fileUtil.Read(filePath, true, cancellationToken);

            var normalized = _yamlUtil.Normalize(yaml);

            var json = _yamlUtil.YamlToJson(normalized);

            await _fileUtil.Write(targetJsonPath, json, true, cancellationToken);
        }

        return jsonDirectory;
    }

    private async ValueTask<string> ConvertOpenApiFileToJson(string filePath, CancellationToken cancellationToken)
    {
        await using MemoryStream stream = await _fileUtil.ReadToMemoryStream(filePath, log: false, cancellationToken);

        ReadResult readResult = await OpenApiDocument.LoadAsync(stream, GetOpenApiFormat(filePath), new OpenApiReaderSettings(), cancellationToken);

        if (readResult.Document == null)
            throw new InvalidOperationException($"Failed to read OpenAPI document '{filePath}'.");

        using var stringWriter = new StringWriter(new StringBuilder(4096));
        var writer = new OpenApiJsonWriter(stringWriter);
        readResult.Document.SerializeAsV3(writer);

        return stringWriter.ToString();
    }

    private static bool IsSupportedOpenApiFile(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetOpenApiFormat(string filePath)
    {
        string extension = Path.GetExtension(filePath);

        return extension.ToLowerInvariant() switch
        {
            ".json" => OpenApiConstants.Json,
            ".yaml" => OpenApiConstants.Yaml,
            ".yml" => OpenApiConstants.Yml,
            _ => throw new InvalidOperationException($"Unsupported OpenAPI file extension: {extension}")
        };
    }

    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");

        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}