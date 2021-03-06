﻿// 
//  Copyright (c) Microsoft Corporation. All rights reserved. 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  

namespace Microsoft.PackageManagement.Providers.Internal.Bootstrap {
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Management.Automation;
    using System.Threading.Tasks;
    using PackageManagement.Internal;
    using PackageManagement.Internal.Api;
    using PackageManagement.Internal.Implementation;
    using PackageManagement.Internal.Packaging;
    using PackageManagement.Packaging;
    using PackageManagement.Internal.Utility.Extensions;
    using PackageManagement.Internal.Utility.Plugin;
    using PackageManagement.Internal.Utility.Versions;
    using Directory = System.IO.Directory;
    using ErrorCategory = PackageManagement.Internal.ErrorCategory;
    using File = System.IO.File;
    using System.IO.Compression;

    public class BootstrapProvider {
        private static readonly Dictionary<string, string[]> _features = new Dictionary<string, string[]> {
            // {Constants.Features.SupportedSchemes, new[] {"http", "https", "file"}},
            // {Constants.Features.SupportedExtensions, new[] {"exe", "msi"}},
            {Constants.Features.MagicSignatures, Constants.Empty},
            {Constants.Features.AutomationOnly, Constants.Empty}
        };
        
        private const WildcardOptions WildcardOptions = System.Management.Automation.WildcardOptions.CultureInvariant | System.Management.Automation.WildcardOptions.IgnoreCase;

        private static IEqualityComparer<Package> PackageEqualityComparer = new PackageManagement.Internal.Utility.Extensions.EqualityComparer<Package>(
            (x, y) => x.Name.EqualsIgnoreCase(y.Name) && x.Version.EqualsIgnoreCase(y.Version), (x) => (x.Name + x.Version).GetHashCode());

        private PackageManagementService PackageManagementService {
            get {
                return PackageManager.Instance as PackageManagementService;
            }
        }

        /// <summary>
        ///     Returns the name of the Provider.
        /// </summary>
        /// <required />
        /// <returns>the name of the package provider</returns>
        public string PackageProviderName {
            get {
                return "Bootstrap";
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "Plugin requirement.")]
        public void InitializeProvider(BootstrapRequest request) {
            // we should go find out what's available once here just to make sure that
            // we have a list
            try {
                request.Debug("Initialize Bootstrapper");
                Task.Factory.StartNew(() => {
                    // we can do this asynchronously, it'll cut down on any startup delay when the network is slow or unavailable.
                    try {
                        PackageManagementService.BootstrappableProviderNames = request.Providers.Select(provider => provider.Name).ToArray();
                    } catch (Exception e) {
                        // if we have a serious problem, it just means we can't bootstrap those providers anyway.
                        // in the event of a catastrophic failure, request isn't going to be valid anymore (and hence the user won't see it)
                        // but we can send the error to the system debug output.
                        e.Dump();
                    }
                });
            } catch (Exception e) {
                e.Dump();
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "Plugin requirement.")]
        public void GetFeatures(BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }

            request.Debug("Calling 'Bootstrap::GetFeatures'");
            foreach (var feature in _features) {
                request.Yield(feature);
            }
        }

        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "Plugin requirement.")]
        public void GetDynamicOptions(string category, BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }

            request.Debug("Calling 'Bootstrap::GetDynamicOptions ({0})'", category);

            switch ((category ?? string.Empty).ToLowerInvariant()) {
                case "package":
                    break;

                case "source":
                    break;

                case "install":
                    request.YieldDynamicOption("DestinationPath", "Folder", false);
                    request.YieldDynamicOption("Scope", "String", false, new[] { "CurrentUser", "AllUsers" });
                    break;
            }
        }

        public void ResolvePackageSources(BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }

            request.Debug("Calling ResolvePackageSources");

            try {
                foreach (var source in request._urls) {

                    request.YieldPackageSource(source.AbsoluteUri, source.AbsoluteUri, false, true, true);
                }
            } catch (Exception e) {
                e.Dump();
            }
        }

        public void FindPackage(string name, string requiredVersion, string minimumVersion, string maximumVersion, int id, BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }

            request.Verbose(Resources.Messages.FindingPackage, string.Format(CultureInfo.CurrentCulture, "{0}::FindPackage' '{1}','{2}','{3}','{4}'", PackageProviderName, name, requiredVersion, minimumVersion, maximumVersion));

            if (name != null && name.EqualsIgnoreCase("PackageManagement")) {
                // they are looking for PackageManagement itself.
                // future todo: let PackageManagement update itself.
                return;
            }

            // are they are looking for a specific provider?
            if (string.IsNullOrWhiteSpace(name) || WildcardPattern.ContainsWildcardCharacters(name)) {
                

                // no, return all providers that match the range.                
                var wildcardPattern = new WildcardPattern(name, WildcardOptions);

                if (request.GetOptionValue("AllVersions").IsTrue()) {                  
                    foreach (var p in request.Providers.Distinct(PackageEqualityComparer).Where(p => string.IsNullOrWhiteSpace(name) || wildcardPattern.IsMatch(p.Name))) {
                        FindPackage(p.Name, null, "0.0", null, 0, request);
                    }
                    return;
                }

                if (request.Providers.Distinct(PackageEqualityComparer).Where(p => string.IsNullOrWhiteSpace(name) || wildcardPattern.IsMatch(p.Name)).Any(p => !request.YieldFromSwidtag(p, requiredVersion, minimumVersion, maximumVersion, name))) {
                    // if there is a problem, exit.
                    return;
                }
            } else {
                // return just the one they asked for.

                // asked for a specific version?
                if (!string.IsNullOrWhiteSpace(requiredVersion)) {
                    request.YieldFromSwidtag(request.GetProvider(name, requiredVersion), name);
                    return;
                }

                if (request.GetOptionValue("AllVersions").IsTrue()) {
                    if (request.GetProviderAll(name, minimumVersion, maximumVersion).Distinct(PackageEqualityComparer).Any(provider => !request.YieldFromSwidtag(provider, name))) {
                        // if there is a problem, exit.
                        return;
                    }
                    return;
                }

                // asked for a version range?
                if (!string.IsNullOrWhiteSpace(minimumVersion) || !string.IsNullOrEmpty(maximumVersion)) {
                    if (request.GetProvider(name, minimumVersion, maximumVersion).Distinct(PackageEqualityComparer).Any(provider => !request.YieldFromSwidtag(provider, name))) {
                        // if there is a problem, exit.
                        return;
                    }
                    return;
                }

                // just return by name
                request.YieldFromSwidtag(request.GetProvider(name), name);
            }

            // return any matches in the name
        }

        /// <summary>
        ///     Returns the packages that are installed
        /// </summary>
        /// <param name="name">the package name to match. Empty or null means match everything</param>
        /// <param name="requiredVersion">
        ///     the specific version asked for. If this parameter is specified (ie, not null or empty
        ///     string) then the minimum and maximum values are ignored
        /// </param>
        /// <param name="minimumVersion">
        ///     the minimum version of packages to return . If the <code>requiredVersion</code> parameter
        ///     is specified (ie, not null or empty string) this should be ignored
        /// </param>
        /// <param name="maximumVersion">
        ///     the maximum version of packages to return . If the <code>requiredVersion</code> parameter
        ///     is specified (ie, not null or empty string) this should be ignored
        /// </param>
        /// <param name="request">
        ///     An object passed in from the CORE that contains functions that can be used to interact with
        ///     the CORE and HOST
        /// </param>
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "Plugin requirement.")]
        public void GetInstalledPackages(string name, string requiredVersion, string minimumVersion, string maximumVersion, BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }

            request.Debug("Calling '{0}::GetInstalledPackages' '{1}','{2}','{3}','{4}'", PackageProviderName, name, requiredVersion, minimumVersion, maximumVersion);

            //search under the providerAssembies folder for the installed providers
            var providers = PackageManagementService.AllProvidersFromProviderAssembliesLocation(request).Select(providerFileAssembly => {
                          
                //get the provider's name\version
                var versionFolder = Path.GetDirectoryName(providerFileAssembly);

                if (string.IsNullOrWhiteSpace(versionFolder)) {
                    return null;
                }
 
                Version ver;
                if (!Version.TryParse(Path.GetFileName(versionFolder), out ver)) {
                    //this will cover whether the providerFileAssembly is at top level as well as a bad version folder
                    //skip if the provider is at the top level as they are imported already via LoadProviders() during the initialization. 
                    //the provider will be handled PackageManagementService.DynamicProviders below.
                    return null;
                }
                                              
                var providerNameFolder = Path.GetDirectoryName(versionFolder);
                if (!string.IsNullOrWhiteSpace(providerNameFolder)) {
                    var providerName = Path.GetFileName(providerNameFolder);
                    if (!string.IsNullOrWhiteSpace(providerName)) {
                        return new {
                            Name = providerName,
                            Version = (FourPartVersion)ver,
                            ProviderPath = providerFileAssembly
                        };
                    }
                }
                
                return null;
            }).WhereNotNull();

            // return all the dynamic package providers as packages
            providers = providers.Concat(PackageManagementService.DynamicProviders.Select(each => new {
                Name = each.ProviderName,
                each.Version,
                each.ProviderPath
            })).Distinct();

            foreach (var provider in providers) {
                // for each package manager, match it's name and version with the swidtag from the remote feed
                var p = request.GetProvider(provider.Name, provider.Version);
                if (p == null) {
                    request.Debug("Dynamic provider '{0}' from '{1}' is not listed in a bootstrap feed.", provider.Name, provider.ProviderPath);
                    // we didn't find it. It's possible that the provider is listed elsewhere.
                    // well, we'll return as much info as we have.
                    continue;
                }
                request.YieldFromSwidtag(p, requiredVersion, minimumVersion, maximumVersion, name);
            }
        }
      
        [SuppressMessage("Microsoft.Design", "CA1011:ConsiderPassingBaseTypesAsParameters", Justification = "Plugin requirement.")]
        public void DownloadPackage(string fastPath, string location, BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }
            request.Debug("Calling 'Bootstrap::DownloadPackage'");
        }

        private bool InstallProviderFromInstaller(Package provider, Link link, string fastPath, BootstrapRequest request) {
            switch (link.MediaType) {
                case Iso19770_2.MediaType.MsiPackage:
                case Iso19770_2.MediaType.MsuPackage:
                    return InstallPackageFile(provider, fastPath, request);

                case Iso19770_2.MediaType.PackageReference:
                    // let the core figure out how to install this package 
                    var packages = PackageManagementService.FindPackageByCanonicalId(link.HRef.AbsoluteUri, request).ToArray();
                    switch (packages.Length) {
                        case 0:
                            request.Warning("Unable to resolve package reference '{0}'", link.HRef);
                            return false;

                        case 1:
                            return InstallPackageReference(provider, fastPath, request, packages);

                        default:
                            request.Warning("Package Reference '{0}' resolves to {1} packages.", packages.Length);
                            return false;
                    }

                case Iso19770_2.MediaType.NuGetPackage:
                    return InstallNugetPackage(provider, link, fastPath, request);

                default:
                    request.Warning("Provider '{0}' with link '{1}' has unknown media type '{2}'.", provider.Name, link.HRef, link.MediaType);
                    return false;
            }
        }

        private bool InstallNugetPackage(Package provider, Link link, string fastPath, BootstrapRequest request)
        {
            // download the nuget package
            string downloadedNupkg = request.DownloadAndValidateFile(provider.Name, provider._swidtag);

            if (downloadedNupkg != null)
            {
                // extracted folder
                string extractedFolder = String.Concat(downloadedNupkg.GenerateTemporaryFilename());

                try
                {
                    //unzip the file
                    ZipFile.ExtractToDirectory(downloadedNupkg, extractedFolder);

                    if (Directory.Exists(extractedFolder))
                    {
                        string versionFolder = Path.Combine(request.DestinationPath(request), provider.Name, provider.Version);
                        // tool folder is where we find things like nuget.exe
                        string toolFolder = Path.Combine(extractedFolder, "tools");
                        string libFolder = Path.Combine(extractedFolder, "lib");

                        // create the directory version folder if not exist
                        if (!Directory.Exists(versionFolder))
                        {
                            Directory.CreateDirectory(versionFolder);
                        }

                        // copy the tools directory
                        if (Directory.Exists(toolFolder))
                        {
                            string destinationToolFolder = Path.Combine(versionFolder, "tools");

                            if (!Directory.Exists(destinationToolFolder))
                            {
                                Directory.CreateDirectory(destinationToolFolder);
                            }

                            foreach (string child in Directory.EnumerateFiles(toolFolder))
                            {
                                try
                                {
                                    // try copy and overwrite
                                    File.Copy(child, Path.Combine(destinationToolFolder, Path.GetFileName(child)), true);
                                }
                                catch (Exception e)
                                {
                                    request.Debug(e.StackTrace);
                                    if (!(e is UnauthorizedAccessException || e is IOException))
                                    {
                                        // something wrong, delete the version folder
                                        versionFolder.TryHardToDelete();
                                        return false;
                                    }

                                    // otherwise this means the file is just being used. so just moves on to copy other files
                                }
                            }
                        }

                        // copy files from lib
                        if (Directory.Exists(libFolder))
                        {
                            // check that the lib folder has at most 1 dll
                            if (Directory.EnumerateFiles(libFolder).Count(file => String.Equals(Path.GetExtension(file), ".dll", StringComparison.OrdinalIgnoreCase)) > 1)
                            {
                                request.Warning(String.Format(CultureInfo.CurrentCulture, Resources.Messages.MoreThanOneDllExists, provider.Name));
                                return false;
                            }

                            foreach (string child in Directory.EnumerateFiles(libFolder))
                            {
                                try
                                {
                                    File.Copy(child, Path.Combine(versionFolder, Path.GetFileName(child)), true);
                                }
                                catch (Exception e)
                                {
                                    request.Debug(e.StackTrace);
                                    if (!(e is UnauthorizedAccessException || e is IOException))
                                    {
                                        // something wrong, delete the version folder
                                        versionFolder.TryHardToDelete();
                                        return false;
                                    }

                                    // otherwise this means the file is just being used. so just moves on to copy other files
                                }
                            }
                        }

                        // target file name is the assembly provider
                        string targetFile = Path.Combine(versionFolder, Path.GetFileName(link.Attributes[Iso19770_2.Discovery.TargetFilename]));

                        if (File.Exists(targetFile))
                        {
                            request.Verbose(Resources.Messages.InstalledPackage, provider.Name, targetFile);
                            request.YieldFromSwidtag(provider, fastPath);
                            return true;
                        }
                    }
                }
                finally
                {
                    downloadedNupkg.TryHardToDelete();
                    extractedFolder.TryHardToDelete();
                }
            }

            return false;
        }

        private bool InstallPackageFile(Package provider, string fastPath, BootstrapRequest request) {
            // we can download and verify this package and get the core to install it.
            var file = request.DownloadAndValidateFile(provider.Name, provider._swidtag);
            if (file != null) {
                // we have a valid file.
                // run the installer
                if (request.ProviderServices.Install(file, "", request)) {
                    // it installed ok!
                    request.YieldFromSwidtag(provider, fastPath);
                    PackageManagementService.LoadProviders(request.As<IRequest>());
                    return true;
                }
                request.Warning(Constants.Messages.FailedProviderBootstrap, fastPath);
            }
            return false;
        }

        private bool InstallPackageReference(Package provider, string fastPath, BootstrapRequest request, SoftwareIdentity[] packages) {
            IHostApi installRequest = request;
            if (packages[0].Provider.Name.EqualsIgnoreCase("PowerShellGet") && !request.ProviderServices.IsElevated) {
                // if we're not elevated, we want powershellget to install to the user scope
            
                installRequest = new object[] {
                    new {
                        GetOptionKeys = new Func<IEnumerable<string>>(() => request.OptionKeys.ConcatSingleItem("Scope")),
                        GetOptionValues = new Func<string, IEnumerable<string>>((key) => {
                            if (key != null && key.EqualsIgnoreCase("Scope")) {
                                return "CurrentUser".SingleItemAsEnumerable();
                            }
                            return request.GetOptionValues(key);
                        })
                    }
                    , installRequest
                }.As<IHostApi>();
            }

            var installing = packages[0].Provider.InstallPackage(packages[0], installRequest);

            SoftwareIdentity lastPackage = null;

            foreach (var i in installing) {
                lastPackage = i;
                // should we echo each package back as it comes back? 
                request.YieldSoftwareIdentity(i.FastPackageReference, i.Name, i.Version, i.VersionScheme, i.Summary, i.Source, i.SearchKey, i.FullPath, i.PackageFilename);

                if (request.IsCanceled) {
                    installing.Cancel();
                }
            }

            if (!request.IsCanceled && lastPackage != null) {
                if (provider.Name.EqualsIgnoreCase("PowerShellGet")) {
                    // special case. PSModules we can just ask the PowerShell provider to pick it up 
                    // rather than try to scan for it.
                    PackageManagementService.TryLoadProviderViaMetaProvider("PowerShell", lastPackage.FullPath, request);
                    request.YieldFromSwidtag(provider, fastPath);
                    return true;
                }

                // looks like it installed ok.
                request.YieldFromSwidtag(provider, fastPath);

                // rescan providers
                PackageManagementService.LoadProviders(request.As<IRequest>());
                return true;
            }
            return false;
        }

        private bool InstallAssemblyProvider(Package provider, Link link, string fastPath, BootstrapRequest request) {
            request.Verbose(Resources.Messages.InstallingPackage, fastPath);
            
            if (!Directory.Exists(request.DestinationPath(request))) {
                request.Error(ErrorCategory.InvalidOperation, fastPath, Constants.Messages.DestinationPathNotSet);
                return false;
            }

            var targetFilename = link.Attributes[Iso19770_2.Discovery.TargetFilename];

            if (string.IsNullOrWhiteSpace(targetFilename)) {
                request.Error(ErrorCategory.InvalidOperation, fastPath, Constants.Messages.InvalidFilename);
                return false;
            }

            targetFilename = Path.GetFileName(targetFilename);

            if (string.IsNullOrWhiteSpace(provider.Version)) {
                request.Error(ErrorCategory.InvalidOperation, fastPath, Resources.Messages.MissingVersion);
                return false;
            }

            //the provider is installing to like this folder: \WindowsPowerShell\Modules\PackageManagement\ProviderAssemblies\nuget\2.8.5.127
            //... providername\version\.dll
            var versionFolder = Path.Combine(request.DestinationPath(request), provider.Name, provider.Version);

            if (!Directory.Exists(versionFolder)) {
                //we create it
                Directory.CreateDirectory(versionFolder);
            }

            var targetFile = Path.Combine(versionFolder, targetFilename);

            // download the file
            var file = request.DownloadAndValidateFile(provider.Name, provider._swidtag);

            if (file != null) {
                try
                {
                    // looks good! let's keep it
                    if (File.Exists(targetFile)) {
                        request.Debug("Removing old file '{0}'", targetFile);
                        targetFile.TryHardToDelete();
                    }

                    // is that file still there?
                    if (File.Exists(targetFile)) {
                        request.Error(ErrorCategory.InvalidOperation, fastPath, Constants.Messages.UnableToRemoveFile, targetFile);
                        return false;
                    }

                    request.Debug("Copying file '{0}' to '{1}'", file, targetFile);
                    try {
                        File.Copy(file, targetFile);
                    }
                    catch (Exception ex) {
                        request.Debug(ex.StackTrace);
                        return false;
                    }

                    //do not need to load the assembly here.The caller in the PackageManangemnt.RequirePackageProvider() is loading assembly
                    //after the install
                    if (File.Exists(targetFile)) {
                        request.Verbose(Resources.Messages.InstalledPackage, provider.Name, targetFile);
                        request.YieldFromSwidtag(provider, fastPath);
                        return true;
                    }

                }
                finally
                {
                    file.TryHardToDelete();
                }
            }

            return false;
        }

        public void InstallPackage(string fastPath, BootstrapRequest request) {
            if (request == null) {
                throw new ArgumentNullException("request");
            }
            // ensure that mandatory parameters are present.
            request.Debug("Calling 'Bootstrap::InstallPackage'");
            var triedAndFailed = false;

            // verify the package integrity (ie, check if it's digitally signed before installing)

            var provider = request.GetProvider(new Uri(fastPath));
            if (provider == null || !provider.IsValid) {
                request.Error(ErrorCategory.InvalidData, fastPath, Constants.Messages.UnableToResolvePackage, fastPath);
                return;
            }

            // group the links along 'artifact' lines
            var artifacts = provider._swidtag.Links.Where(link => link.Relationship == Iso19770_2.Relationship.InstallationMedia).GroupBy(link => link.Artifact);

            // try one artifact set at a time.
            foreach (var artifact in artifacts) {
                // first time we succeed, we're good to go.
                foreach (var link in artifact) {
                    switch (link.Attributes[Iso19770_2.Discovery.Type]) {
                        case "assembly":
                            if (InstallAssemblyProvider(provider, link, fastPath, request)) {
                                return;
                            }
                            triedAndFailed = true;
                            continue;

                        default:
                            if (InstallProviderFromInstaller(provider, link, fastPath, request)) {
                                return;
                            }
                            triedAndFailed = true;
                            continue;
                    }
                }
            }

            if (triedAndFailed) {
                // we tried installing something and it didn't go well.
                request.Error(ErrorCategory.InvalidOperation, fastPath, Constants.Messages.FailedProviderBootstrap, fastPath);
            } else {
                // we didn't even find a link to bootstrap.
                request.Error(ErrorCategory.InvalidOperation, fastPath, "Provider {0} missing installationmedia to install.", fastPath);
            }
        }
    }
}