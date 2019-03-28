﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Esprima;
using Esprima.Ast;
using Karambolo.AspNetCore.Bundling.Internal.Helpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Karambolo.AspNetCore.Bundling.EcmaScript.Internal
{
    internal partial class ModuleBundler : IModuleBundler
    {
        public const string DefaultExportName = "default";

        private static readonly char[] s_slashAndDot = new[] { '/', '.' };

        private static string NormalizePath(string path)
        {
            return UrlUtils.NormalizePath(path, canonicalize: true);
        }

        private static string NormalizeModulePath(string basePath, string path)
        {
            var index = path.LastIndexOfAny(s_slashAndDot);
            if (index < 0 || path[index] != '.')
                path += ".js";

            return NormalizePath(path.StartsWith('/') ? path : basePath + path);
        }

        private string GetFileProviderPrefix(ModuleFile moduleFile)
        {
            return _fileProviderPrefixes[moduleFile.FileProvider];
        }

        private static string GetFileProviderHint(ModuleFile moduleFile)
        {
            return
                moduleFile.FileProvider is PhysicalFileProvider physicalFileProvider ?
                $"{physicalFileProvider.GetType().Name}[{physicalFileProvider.Root}]" :
                moduleFile.FileProvider.GetType().Name;
        }

        private readonly ILogger _logger;
        private readonly string _br;
        private readonly bool _developmentMode;

        private Dictionary<ModuleFile, ModuleData> _modules;
        private Dictionary<IFileProvider, string> _fileProviderPrefixes;

        public ModuleBundler(ILoggerFactory loggerFactory = null, ModuleBundlerOptions options = null)
        {
            _logger = loggerFactory?.CreateLogger<ModuleBundler>() ?? (ILogger)NullLogger.Instance;
            _br = options?.NewLine ?? Environment.NewLine;
            _developmentMode = options?.DevelopmentMode ?? false;
        }

        private async Task LoadModuleContent(ModuleData module)
        {
            try
            {
                using (Stream stream = module.File.GetFileInfo().CreateReadStream())
                using (var reader = new StreamReader(stream))
                    module.Content = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
            catch (Exception ex) { throw new ModuleBundlingErrorException($"Failed to read file {module.FilePath} via {GetFileProviderHint(module.File)}.", ex); }
        }

        private static Program ParseModuleContent(ModuleData module)
        {
            var parser = new JavaScriptParser(module.Content, new ParserOptions { Loc = true, Range = true, SourceType = SourceType.Module, Tolerant = true });
            try { return parser.ParseProgram(); }
            catch (Exception ex) { throw new ModuleBundlingErrorException($"Failed to parse file {module.FilePath} provided by {GetFileProviderHint(module.File)}.", ex); }
        }

        private async Task LoadModuleCoreAsync(ModuleData module, CancellationTokenSource errorCts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                module.Ast = ParseModuleContent(module);
                module.ModuleRefs = new Dictionary<ModuleFile, string>();
                module.ExportsRaw = new List<ExportData>();
                module.Imports = new Dictionary<string, ImportData>();

                AnalyzeDeclarations(module);

                if (module.ModuleRefs.Count == 0)
                    return;

                var loadModuleTasks = new List<Task>();
                foreach (ModuleFile moduleFile in module.ModuleRefs.Keys)
                {
                    lock (_modules)
                        if (!_modules.ContainsKey(moduleFile))
                            _modules.Add(moduleFile, module = new ModuleData(moduleFile));
                        else
                            continue;

                    loadModuleTasks.Add(LoadModuleAsync(module, errorCts, cancellationToken));
                }
                await Task.WhenAll(loadModuleTasks).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                errorCts.Cancel(); // stop loading process
                throw;
            }
        }

        private async Task LoadModuleAsync(ModuleData module, CancellationTokenSource errorCts, CancellationToken cancellationToken)
        {
            await LoadModuleContent(module).ConfigureAwait(false);
            await LoadModuleCoreAsync(module, errorCts, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ModuleBundlingResult> BundleAsync(ModuleFile[] rootFiles, CancellationToken cancellationToken = default)
        {
            if (rootFiles == null)
                throw new ArgumentNullException(nameof(rootFiles));

            rootFiles = rootFiles.ToArray();

            _modules = new Dictionary<ModuleFile, ModuleData>();
            _fileProviderPrefixes = new Dictionary<IFileProvider, string>();
            int fileProviderId = 0;
            try
            {
                for (int i = 0, n = rootFiles.Length; i < n; i++)
                {
                    ModuleFile moduleFile = rootFiles[i];

                    if (_modules.ContainsKey(moduleFile))
                        continue;

                    rootFiles[i] = moduleFile = new ModuleFile(
                        moduleFile.FileProvider,
                        moduleFile.FilePath != null ? NormalizePath(moduleFile.FilePath) : "<>" + i,
                        moduleFile.CaseSensitivePaths);

                    if (!_fileProviderPrefixes.ContainsKey(moduleFile.FileProvider))
                        _fileProviderPrefixes.Add(moduleFile.FileProvider, fileProviderId++.ToString() + ':');

                    var module = new ModuleData(moduleFile);

                    if (module.Content == null)
                        await LoadModuleContent(module).ConfigureAwait(false);

                    _modules.Add(moduleFile, module);
                }

                if (_fileProviderPrefixes.Count == 1)
                    _fileProviderPrefixes[_fileProviderPrefixes.Keys.First()] = string.Empty;

                // 1) analyze content

                using (var errorCts = new CancellationTokenSource())
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(errorCts.Token, cancellationToken))
                    await Task.WhenAll(_modules.Values.ToArray().Select(module => LoadModuleCoreAsync(module, errorCts, linkedCts.Token))).ConfigureAwait(false);

                // 2) synthesize result

                try
                {
                    Parallel.ForEach(_modules.Values, new ParallelOptions { CancellationToken = cancellationToken },
                        () => new RewriteModuleLocals(),
                        RewriteModule,
                        _ => { });
                }
                catch (AggregateException ex)
                {
                    // unwrap and re-throw aggregate exception
                    ExceptionDispatchInfo.Capture(ex.Flatten().InnerException).Throw();
                }

                cancellationToken.ThrowIfCancellationRequested();

                return BuildResult(rootFiles);
            }
            catch (ModuleBundlingErrorException ex)
            {
                _logger.LogError(ex, ex.Message);
                return ModuleBundlingResult.Failure;
            }
            finally
            {
                _modules = null;
                _fileProviderPrefixes = null;
            }
        }
    }
}