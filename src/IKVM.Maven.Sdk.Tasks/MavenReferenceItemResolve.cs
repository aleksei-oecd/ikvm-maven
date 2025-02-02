﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using IKVM.Maven.Sdk.Tasks.Aether;
using IKVM.Maven.Sdk.Tasks.Resources;

using java.util;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Newtonsoft.Json;

using org.apache.maven.artifact.resolver;
using org.apache.maven.artifact.versioning;
using org.eclipse.aether;
using org.eclipse.aether.artifact;
using org.eclipse.aether.collection;
using org.eclipse.aether.graph;
using org.eclipse.aether.repository;
using org.eclipse.aether.resolution;
using org.eclipse.aether.util.artifact;
using org.eclipse.aether.util.filter;

namespace IKVM.Maven.Sdk.Tasks
{

    /// <summary>
    /// For each <see cref="MavenReferenceItem"/>, resolves the full set of MavenReferenceItem's that should be generated.
    /// </summary>
    public class MavenReferenceItemResolve : Task
    {

        static readonly JsonSerializer serializer = new()
        {
            Converters =
            {
                new DefaultArtifactJsonConverter(),
                new DefaultDependencyNodeJsonConverter(),
                new DependencyJsonConverter(),
                new ExclusionJsonConverter(),
                new RemoteRepositoryJsonConverter(),
                new VersionJsonConverter(),
                new VersionConstraintJsonConverter(),
            }
        };

        static readonly java.lang.Boolean TRUE = java.lang.Boolean.TRUE;
        static readonly java.lang.Boolean FALSE = java.lang.Boolean.FALSE;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        public MavenReferenceItemResolve() :
            base(SR.ResourceManager, "MAVEN:")
        {

        }

        /// <summary>
        /// Path to the cache file.
        /// </summary>
        [Required]
        public string CacheFile { get; set; }

        /// <summary>
        /// Set of Maven repostories to initialize.
        /// </summary>
        [Required]
        public ITaskItem[] Repositories { get; set; }

        /// <summary>
        /// Set of MavenReferenceItem.
        /// </summary>
        [Required]
        public ITaskItem[] References { get; set; }

        /// <summary>
        /// Set of output IkvmReferenceItem instances.
        /// </summary>
        [Output]
        public ITaskItem[] ResolvedReferences { get; set; }

        /// <summary>
        /// Name of the classloader to use for the reference items.
        /// </summary>
        public string ClassLoader { get; set; }

        /// <summary>
        /// Value to set for Debug on generated references unless otherwise specified.
        /// </summary>
        public bool Debug { get; set; } = false;

        /// <summary>
        /// Path to the key file to use for signing Maven assemblies.
        /// </summary>
        public string KeyFile { get; set; }

        /// <summary>
        /// Indicates whether the resolution should include test items.
        /// </summary>
        public bool IncludeTestScope { get; set; }

        /// <summary>
        /// Attempts to read the cache file.
        /// </summary>
        /// <returns></returns>
        MavenResolveCacheFile TryReadCacheFile()
        {
            if (CacheFile != null && File.Exists(CacheFile))
            {
                using var stm = File.OpenRead(CacheFile);
                using var rdr = new StreamReader(stm);
                using var jsn = new JsonTextReader(rdr);
                return serializer.Deserialize<MavenResolveCacheFile>(jsn);
            }

            return null;
        }

        /// <summary>
        /// Attempts to write the cache file.
        /// </summary>
        /// <returns></returns>
        void TryWriteCacheFile(MavenResolveCacheFile cacheFile)
        {
            if (CacheFile != null)
            {
                using var stm = File.Create(CacheFile);
                using var wrt = new StreamWriter(stm);
                using var jsn = new JsonTextWriter(wrt) { Formatting = Formatting.Indented };
                serializer.Serialize(jsn, cacheFile);
            }
        }

        /// <summary>
        /// Executes the task.
        /// </summary>
        /// <returns></returns>
        public override bool Execute()
        {
            try
            {
                var repositories = MavenRepositoryItemMetadata.Load(Repositories);
                var items = MavenReferenceItemMetadata.Load(References);
                ResolvedReferences = ResolveReferences(repositories, items).Select(ToTaskItem).ToArray();
                return true;
            }
            catch (MavenTaskMessageException e)
            {
                Log.LogErrorWithCodeFromResources(e.MessageResourceName, e.MessageArgs);
                return false;
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true, true, null);
                return false;
            }
        }

        /// <summary>
        /// Persists the item to a task item.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        ITaskItem ToTaskItem(IkvmReferenceItem item)
        {
            var task = new TaskItem();
            IkvmReferenceItemMetadata.Save(item, task);
            return task;
        }

        /// <summary>
        /// Resolves the set of dependencies given by the set of items.
        /// </summary>
        /// <returns></returns>
        IEnumerable<IkvmReferenceItem> ResolveReferences(IList<MavenRepositoryItem> repositories, IList<MavenReferenceItem> items)
        {
            if (repositories == null)
                throw new ArgumentNullException(nameof(repositories));
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            var maven = new IkvmMavenEnvironment(repositories, Log);
            var session = maven.CreateRepositorySystemSession(false);

            // root of the runtime dependency graph
            var graph = ResolveCompileDependencyGraph(maven, session, repositories, items);
            if (graph == null)
                throw new NullReferenceException("Null result obtaining dependency graph.");

            // walk the full dependency graph to generate items and their references
            var output = new Dictionary<string, IkvmReferenceItem>();
            CollectIkvmReferenceItems(output, graph);

            // resolve compile and runtime items and ensure they are copied
            var privateScopes = new List<string>() { JavaScopes.COMPILE, JavaScopes.RUNTIME };
            if (IncludeTestScope)
                privateScopes.Add(JavaScopes.TEST);
            foreach (var ikvmItem in ResolveIkvmReferenceItemsForScopes(output, maven, session, graph, privateScopes))
                ikvmItem.Private = true;

            // resolve compile and provided items and ensure they are referenced
            var referenceOutputAssemblyScopes = new List<string>() { JavaScopes.COMPILE, JavaScopes.PROVIDED };
            if (IncludeTestScope)
                referenceOutputAssemblyScopes.Add(JavaScopes.TEST);
            foreach (var ikvmItem in ResolveIkvmReferenceItemsForScopes(output, maven, session, graph, referenceOutputAssemblyScopes))
                ikvmItem.ReferenceOutputAssembly = true;

            return output.Values;
        }

        /// <summary>
        /// Resolves the dependency graph for any items that may be relevant to the code of the application.
        /// </summary>
        /// <param name="maven"></param>
        /// <param name="session"></param>
        /// <param name="repositories"></param>
        /// <param name="items"></param>
        /// <returns></returns>
        DependencyNode ResolveCompileDependencyGraph(IkvmMavenEnvironment maven, RepositorySystemSession session, IList<MavenRepositoryItem> repositories, IList<MavenReferenceItem> items)
        {
            if (maven is null)
                throw new ArgumentNullException(nameof(maven));
            if (session is null)
                throw new ArgumentNullException(nameof(session));
            if (repositories is null)
                throw new ArgumentNullException(nameof(repositories));
            if (items is null)
                throw new ArgumentNullException(nameof(items));

            // convert set of incoming items into a dependency list
            var dependencies = new Dependency[items.Count];
            for (int i = 0; i < items.Count; i++)
                dependencies[i] = new Dependency(new DefaultArtifact(items[i].GroupId, items[i].ArtifactId, items[i].Classifier, "jar", items[i].Version), items[i].Scope, items[i].Optional ? TRUE : FALSE, new java.util.ArrayList());

            // check the cache
            var root = ResolveCompileDependencyGraphFromCache(maven, dependencies);
            if (root != null)
            {
                Log.LogMessageFromText("Resolved Maven dependency graph from project cache.", MessageImportance.Low);
                return root;
            }

            // collect the full dependency graph
            var filter = DependencyFilterUtils.classpathFilter(JavaScopes.COMPILE, JavaScopes.RUNTIME, JavaScopes.COMPILE, JavaScopes.PROVIDED);
            if (IncludeTestScope)
                filter = DependencyFilterUtils.orFilter(DependencyFilterUtils.classpathFilter(JavaScopes.TEST));
            var result = maven.RepositorySystem.resolveDependencies(
                session,
                new DependencyRequest(
                    new CollectRequest(Arrays.asList(dependencies), null, maven.Repositories),
                    filter));

            root = (DefaultDependencyNode)result.getRoot();
            if (root == null)
                throw new MavenTaskException("Null dependency graph.");

            TryWriteCacheFile(new MavenResolveCacheFile()
            {
                Version = 1,
                Dependencies = dependencies,
                Repositories = repositories.ToArray(),
                Graph = root,
            });

            return root;
        }

        /// <summary>
        /// Attempts to resolve the dependency graph from the cache file.
        /// </summary>
        /// <param name="maven"></param>
        /// <param name="dependencies"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        DefaultDependencyNode ResolveCompileDependencyGraphFromCache(IkvmMavenEnvironment maven, IList<Dependency> dependencies)
        {
            if (maven is null)
                throw new ArgumentNullException(nameof(maven));
            if (dependencies is null)
                throw new ArgumentNullException(nameof(dependencies));

            var cacheFile = TryReadCacheFile();
            if (cacheFile == null)
                return null;

            // current version
            if (cacheFile.Version != 1)
                return null;

            // nothing was cached
            if (cacheFile.Graph == null)
                return null;

            // check that the same set of repositories are involved
            if (cacheFile.Repositories == null || UnorderedSequenceEquals(((IEnumerable)maven.Repositories).Cast<RemoteRepository>().ToArray(), cacheFile.Repositories) == false)
                return null;

            // check that the same set of dependencies are involved
            if (cacheFile.Dependencies == null || UnorderedSequenceEquals(dependencies, cacheFile.Dependencies) == false)
                return null;

            // return previously resolved graph
            return cacheFile.Graph;
        }

        /// <summary>
        /// Checks that each item in <paramref name="a"/> exists in <paramref name="b"/>.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        bool UnorderedSequenceEquals(IList<RemoteRepository> a, IList<MavenRepositoryItem> b)
        {
            if (ReferenceEquals(a, b))
                return true;

            if (b == null)
                return false;

            if (a.Count != b.Count)
                return false;

            foreach (var i in a)
                if (b.Any(j => j.Id == i.getId() && j.Url == i.getUrl()) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Checks that each item in <paramref name="a"/> exists in <paramref name="b"/>.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <returns></returns>
        bool UnorderedSequenceEquals(IList<Dependency> a, IList<Dependency> b)
        {
            if (ReferenceEquals(a, b))
                return true;

            if (b == null)
                return false;

            if (a.Count != b.Count)
                return false;

            foreach (var i in a)
                if (b.Any(j => DependencyEqualityComparer.Default.Equals(i, j)) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Attempts to resolve the source artifact for the specified <see cref="IkvmReferenceItem"/>.
        /// </summary>
        /// <param name="maven"></param>
        /// <param name="session"></param>
        /// <param name="item"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        Artifact ResolveSourceArtifact(IkvmMavenEnvironment maven, RepositorySystemSession session, IkvmReferenceItem item)
        {
            if (maven is null)
                throw new ArgumentNullException(nameof(maven));
            if (session is null)
                throw new ArgumentNullException(nameof(session));
            if (item is null)
                throw new ArgumentNullException(nameof(item));

            try
            {
                var result = maven.RepositorySystem.resolveArtifact(
                    session,
                    new ArtifactRequest(
                        new DefaultArtifact(
                            item.MavenGroupId,
                            item.MavenArtifactId,
                            string.IsNullOrWhiteSpace(item.MavenClassifier) ? "sources" : item.MavenClassifier + "-sources",
                            "jar",
                            item.MavenVersion),
                        maven.Repositories,
                        null));
                if (result.isResolved() == false)
                    return null;
                if (result.getArtifact() is not Artifact artifact)
                    return null;

                return artifact;
            }
            catch (org.eclipse.aether.resolution.ArtifactResolutionException)
            {
                return null;
            }
            catch (ArtifactNotFoundException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves the <see cref="IkvmReferenceItem"/>s that are applicable for the given dependency set and scopes.
        /// </summary>
        /// <param name="output"></param>
        /// <param name="maven"></param>
        /// <param name="session"></param>
        /// <param name="root"></param>
        /// <param name="scopes"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        IEnumerable<IkvmReferenceItem> ResolveIkvmReferenceItemsForScopes(Dictionary<string, IkvmReferenceItem> output, IkvmMavenEnvironment maven, RepositorySystemSession session, DependencyNode root, List<string> scopes)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (maven is null)
                throw new ArgumentNullException(nameof(maven));
            if (session is null)
                throw new ArgumentNullException(nameof(session));
            if (root is null)
                throw new ArgumentNullException(nameof(root));
            if (scopes is null)
                throw new ArgumentNullException(nameof(scopes));

            var result = maven.RepositorySystem.resolveDependencies(
                session,
                new DependencyRequest(root, DependencyFilterUtils.classpathFilter(scopes.ToArray())));
            foreach (ArtifactResult resultItem in (IEnumerable)result.getArtifactResults())
                if (GetIkvmReferenceItemForArtifact(output, resultItem.getArtifact()) is IkvmReferenceItem ikvmItem)
                    yield return ikvmItem;
        }

        /// <summary>
        /// Collects any <see cref="IkvmReferenceItem"/>s from the given node.
        /// </summary>
        /// <param name="output"></param>
        /// <param name="node"></param>
        void CollectIkvmReferenceItems(Dictionary<string, IkvmReferenceItem> output, DependencyNode node)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            // if artifact, obtain IkvmReferenceItem from artifact
            var artifact = node.getArtifact();
            var ikvmItem = artifact != null ? GetIkvmReferenceItemForArtifact(output, artifact) : null;

            // walk tree and ensure IkvmReferenceItem exists for each child
            foreach (DependencyNode child in (IEnumerable)node.getChildren())
                if (child.getDependency().getScope() is JavaScopes.COMPILE or JavaScopes.PROVIDED)
                    CollectIkvmReferenceItems(output, child);

            // if we've got an actual item, traverse it's dependencies to assign references
            if (ikvmItem != null)
                foreach (var ikvmReference in CollectIkvmReferenceItemReferences(output, node))
                    if (ikvmItem.References.Contains(ikvmReference) == false)
                        ikvmItem.References.Add(ikvmReference);
        }

        /// <summary>
        /// Gets the <see cref="IkvmReferenceItem"/> associated with the given artifact.
        /// </summary>
        /// <param name="output"></param>
        /// <param name="artifact"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        IkvmReferenceItem GetIkvmReferenceItemForArtifact(Dictionary<string, IkvmReferenceItem> output, Artifact artifact)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (artifact is null)
                throw new ArgumentNullException(nameof(artifact));

            // we only process JAR artifacts
            var extension = artifact.getExtension();
            if (extension != "jar")
                return null;

            // pull items out of artifact
            var groupId = artifact.getGroupId();
            var artifactId = artifact.getArtifactId();
            var classifier = artifact.getClassifier();
            var version = artifact.getVersion();

            // find or create the IkvmReferenceItem for the artifact
            var ikvmItemSpec = GetIkvmItemSpec(groupId, artifactId, classifier, version);
            if (output.TryGetValue(ikvmItemSpec, out var ikvmItem))
                return ikvmItem;

            // create a new item
            ikvmItem = new IkvmReferenceItem() { ItemSpec = ikvmItemSpec, ReferenceOutputAssembly = false, Private = false };
            output.Add(ikvmItemSpec, ikvmItem);

            // ensure output item has Maven information attached to it
            ikvmItem.MavenGroupId = groupId;
            ikvmItem.MavenArtifactId = artifactId;
            ikvmItem.MavenClassifier = classifier;
            ikvmItem.MavenVersion = version;

            // fallback to the Maven name and version if IKVM cannot detect otherwise
            ikvmItem.FallbackAssemblyName = artifactId;
            ikvmItem.FallbackAssemblyVersion = ToAssemblyVersion(version)?.ToString();

            // inherit global settings
            ikvmItem.Debug = Debug;
            ikvmItem.KeyFile = KeyFile;

            // setup the class loader
            ikvmItem.ClassLoader = ClassLoader;

            // if the artifact is a jar, we need to associate the path to the jar to the item
            var file = artifact.getFile();
            var filePath = file.getAbsolutePath();
            if (ikvmItem.Compile.Contains(filePath) == false)
                ikvmItem.Compile.Add(filePath);

            return ikvmItem;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="output"></param>
        /// <param name="node"></param>
        /// <returns></returns>
        IEnumerable<IkvmReferenceItem> CollectIkvmReferenceItemReferences(Dictionary<string, IkvmReferenceItem> output, DependencyNode node)
        {
            if (output is null)
                throw new ArgumentNullException(nameof(output));
            if (node is null)
                throw new ArgumentNullException(nameof(node));

            foreach (DependencyNode child in (IEnumerable)node.getChildren())
            {
                // if the child node is a direct artifact
                if (child.getArtifact() is Artifact artifact)
                    if (GetIkvmReferenceItemForArtifact(output, artifact) is IkvmReferenceItem reference)
                        yield return reference;

                // recurse into child
                foreach (var reference in CollectIkvmReferenceItemReferences(output, child))
                    yield return reference;
            }
        }

        /// <summary>
        /// Returns a normalized version of a <see cref="MavenReferenceItem"/> itemspec.
        /// </summary>
        /// <param name="groupId"></param>
        /// <param name="artifactId"></param>
        /// <param name="classifier"></param>
        /// <param name="version"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        string GetIkvmItemSpec(string groupId, string artifactId, string classifier, string version)
        {
            if (string.IsNullOrWhiteSpace(groupId))
                throw new ArgumentException($"'{nameof(groupId)}' cannot be null or whitespace.", nameof(groupId));
            if (string.IsNullOrWhiteSpace(artifactId))
                throw new ArgumentException($"'{nameof(artifactId)}' cannot be null or whitespace.", nameof(artifactId));
            if (string.IsNullOrWhiteSpace(version))
                throw new ArgumentException($"'{nameof(version)}' cannot be null or whitespace.", nameof(version));

            var b = new StringBuilder("maven$");
            b.Append(groupId);
            b.Append(':').Append(artifactId);
            if (string.IsNullOrWhiteSpace(classifier) == false)
                b.Append(':').Append(classifier);
            b.Append(':').Append(version);

            return b.ToString();
        }

        /// <summary>
        /// Parses the given Maven version into an assembly version.
        /// </summary>
        /// <param name="version"></param>
        /// <returns></returns>
        Version ToAssemblyVersion(string version)
        {
            try
            {
                var v = new DefaultArtifactVersion(version);
                return new Version(v.getMajorVersion(), v.getMinorVersion());
            }
            catch (Exception)
            {
                return null;
            }
        }

    }

}
