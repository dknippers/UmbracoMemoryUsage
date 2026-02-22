using Microsoft.Data.SqlClient;
using NPoco;
using NPoco.DatabaseTypes;
using NPoco.SqlServer;
using System.Data;
using System.Globalization;
using System.Text;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;
using Umbraco.Cms.Infrastructure.Persistence.Dtos;
using Umbraco.Cms.Web.Common.Profiler;

namespace UmbracoMemoryUsage;

public class MigrationsComposer : ComponentComposer<MigrationsComponent> { }

public class MigrationsComponent
(
    IMigrationPlanExecutor migrationPlanExecutor,
    ICoreScopeProvider coreScopeProvider,
    IKeyValueService keyValueService,
    IRuntimeState runtimeState
) : IAsyncComponent
{
    public async Task InitializeAsync(bool isRestarting, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run)
        {
            return;
        }

        var plan = new MigrationPlan("UmbracoMemoryUsage");

        plan.From(string.Empty)
            .To<DisableProfiler>("DisableProfiler")
            .To<CreateLanguages>("CreateLanguages")
            .To<CreateContentType>("CreateContentType")
            .To<CreateNodes>("CreateNodes");

        var upgrader = new Upgrader(plan);
        await upgrader.ExecuteAsync(migrationPlanExecutor, coreScopeProvider, keyValueService);
    }

    public Task TerminateAsync(bool isRestarting, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private class DisableProfiler
    (
        IMigrationContext context,
        IProfiler profiler
    ) : AsyncMigrationBase(context)
    {
        protected override async Task MigrateAsync()
        {
            if (profiler is WebProfiler webProfiler)
            {
                // The WebProfiler uses huge amounts of memory so
                // it must be stopped when we are running this migration
                // which is only once.
                webProfiler.StopBoot();
            }
        }
    }

    private class CreateLanguages
    (
        IMigrationContext context,
        ILanguageService languageService,
        ICultureService cultureService,
        IConfiguration config,
        ILogger<CreateLanguages> logger
    ) : AsyncMigrationBase(context)
    {
        protected override async Task MigrateAsync()
        {
            var languages = (await languageService.GetAllAsync()).ToArray();

            if (languages.Length > 1)
            {
                // Remove all non-default languages
                foreach (var language in languages.Where(lang => !lang.IsDefault))
                {
                    await languageService.DeleteAsync(language.IsoCode, Constants.Security.SuperUserKey);
                }
            }

            var languageAmount = ConfigReader.GetRequiredInt(config, "UmbracoMemoryUsage:LanguageAmount");
            var defaultLanguage = languages.First(lang => lang.IsDefault);
            var cultureInfos = cultureService.GetValidCultureInfos()
                .Where(ci => !string.IsNullOrWhiteSpace(ci.Name) && !string.IsNullOrWhiteSpace(ci.EnglishName) && ci.Name != defaultLanguage.IsoCode)
                .OrderBy(ci => Guid.NewGuid()) // Random order
                .Take(languageAmount - 1)
                .ToArray();

            logger.LogInformation("[UmbracoMemoryUsage] Default language: {isoCode} | {languageName}", defaultLanguage.IsoCode, defaultLanguage.CultureName);

            if (cultureInfos.Length == 0)
            {
                logger.LogInformation("[UmbracoMemoryUsage] No additional languages will be created");

                return;
            }

            logger.LogInformation("[UmbracoMemoryUsage] Creating additional languages: {amount} ...", cultureInfos.Length);

            foreach (var ci in cultureInfos)
            {
                var language = new Language(ci.Name, ci.EnglishName);
                var attempt = await languageService.CreateAsync(language, Constants.Security.SuperUserKey);
            }
        }
    }

    private class CreateContentType
    (
        IMigrationContext context,
        IContentTypeService contentTypeService,
        ITemplateService templateService,
        IShortStringHelper shortStringHelper,
        ILogger<CreateContentType> logger
    ) : AsyncMigrationBase(context)
    {
        protected override async Task MigrateAsync()
        {
            if (contentTypeService.Get(Page.ModelTypeAlias) is not null)
            {
                // Already exists
                return;
            }

            logger.LogInformation("[UmbracoMemoryUsage] Creating content type {name} ...", nameof(Page));

            var contentType = new ContentType(shortStringHelper, -1)
            {
                Alias = Page.ModelTypeAlias,
                Name = "Page",
                AllowedAsRoot = true,
                Icon = "icon-document",
                Variations = ContentVariation.Culture,
            };

            await contentTypeService.CreateAsync(contentType, Constants.Security.SuperUserKey);

            contentType.AllowedContentTypes = [new ContentTypeSort(contentType.Key, 0, Page.ModelTypeAlias)];

            var templateName = "Page";
            var templateAlias = "page";
            var templateOperation = await templateService.CreateForContentTypeAsync(templateName, templateAlias, Page.ModelTypeAlias, Constants.Security.SuperUserKey);
            if (templateOperation.Success &&
                templateOperation.Result is ITemplate template)
            {
                contentType.AllowedTemplates = [template];
                contentType.DefaultTemplateId = template.Id;
            }
            else
            {
                Logger.LogWarning("Could not create a template for Page");
            }

            await contentTypeService.UpdateAsync(contentType, Constants.Security.SuperUserKey);
        }
    }

    private class CreateNodes
    (
        IMigrationContext context,
        ILanguageService languageService,
        IConfiguration config,
        IContentTypeService contentTypeService,
        IShortStringHelper shortStringHelper,
        ITemplateService templateService,
        ILogger<CreateNodes> logger
    ) : AsyncMigrationBase(context)
    {
        protected override async Task MigrateAsync()
        {
            ILanguage[] languages = [.. await languageService.GetAllAsync()];

            var contentTypeId = contentTypeService.Get(Page.ModelTypeAlias)?.Id ?? throw new InvalidOperationException("Page content type not found");
            var templateId = (await templateService.GetAsync(Page.ModelTypeAlias))?.Id ?? throw new InvalidOperationException("Page template not found");

            var children = ConfigReader.GetRequiredInt(config, "UmbracoMemoryUsage:Children");
            var depth = ConfigReader.GetRequiredInt(config, "UmbracoMemoryUsage:Depth");

            // Whether to publish or only save the nodes
            bool publish = true;

            // Total nodes to create, excluding the root.
            var total = children == 1 ? depth : (int)(Math.Pow(children, depth + 1) - children) / (children - 1);

            // Rows to insert for 1 node:
            // umbracoNode: 1
            // umbracoContent: 1
            // umbracoContentVersion: publish ? 2 : 1
            // umbracoContentVersionCultureVariation: languages * (publish ? 2 : 1)
            // umbracoDocument: 1
            // umbracoDocumentCultureVariation: languages * 1
            // umbracoDocumentUrl: languages * (publish ? 2 : 1)
            // umbracoDocumentVersion: publish ? 2 : 1
            var rowsPerNode = publish
                ? 7 + languages.Length * 5
                : 5 + languages.Length * 3;

            // Total includes the root
            var totalRows = (total + 1) * rowsPerNode;
            var insertedRows = 0;
            var lastPercentage = 0;

            INodeCreator nodeCreator = GetNodeCreator();
            nodeCreator.Prepare(Database);

            var rootNodeId = CreateRootNode(publish);

            var sbNamePrefix = new StringBuilder();

            var sbPath = new StringBuilder();
            sbPath
                .Append(Constants.System.Root)
                .Append(',')
                .Append(rootNodeId);

            var formattedTotal = total.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", "_");
            logger.LogInformation("[UmbracoMemoryUsage] Creating {total} nodes under the root node (children: {children}, depth: {depth}) ... ", formattedTotal, children, depth);

            CreateNodes(rootNodeId, publish, children, depth, 1, sbNamePrefix, sbPath);

            nodeCreator.Finish(Database, ReportProgress);

            // Note: we can only configure domains after we are sure the root node is inserted
            // into the database since we reference its primary key. Some INodeCreator implementations
            // will only insert into the database on Finish() hence we can only do this here.
            logger.LogInformation("[UmbracoMemoryUsage] Configuring domains ...");
            ConfigureDomains(Database, rootNodeId, languages);

            // Since we bypass Umbraco's services we need to explicitly rebuild the cache afterwards.
            RebuildCache = true;

            INodeCreator GetNodeCreator()
            {
                return Database.DatabaseType switch
                {
                    SQLiteDatabaseType => new SqliteNodeCreator(),
                    SqlServerDatabaseType => new SqlServerNodeCreator(),

                    _ => throw new NotSupportedException($"Database type '{Database.DatabaseType}' is not supported")
                };
            }

            void ReportProgress(int amount)
            {
                insertedRows += amount;

                int percentage = (int)(insertedRows / (double)totalRows * 100);
                if (percentage > lastPercentage) // only log when a new percent is reached
                {
                    lastPercentage = percentage;
                    logger.LogInformation("[UmbracoMemoryUsage] {percentage}% complete", percentage);
                }
            }

            int CreateRootNode(bool publish)
            {
                logger.LogInformation("[UmbracoMemoryUsage] Creating root node ...");

                var name = "Root";
                var urlSegment = name.ToUrlSegment(shortStringHelper);
                var rootNodeId = nodeCreator.CreateNode(
                    Database, name, urlSegment, Constants.System.Root, Constants.System.RootString,
                    publish, contentTypeId, templateId, languages, 0, ReportProgress);

                return rootNodeId;
            }

            void CreateNodes(int parentId, bool publish, int children, int depth, int currentDepth, StringBuilder sbNamePrefix, StringBuilder sbPath)
            {
                if (currentDepth > depth)
                    return;

                var sbNamePrefixLength = sbNamePrefix.Length;
                var sbPathLength = sbPath.Length;

                for (int i = 0; i < children; i++)
                {
                    sbNamePrefix.Append(i + 1);

                    // Use the builder's current string representation for the name
                    var name = $"Page {sbNamePrefix}";

                    var urlSegment = name.ToUrlSegment(shortStringHelper);
                    var childId = nodeCreator.CreateNode(
                        Database, name, urlSegment, parentId, sbPath.ToString(),
                        publish, contentTypeId, templateId, languages, i, ReportProgress);

                    sbPath.Append(',').Append(childId);

                    sbNamePrefix.Append('.');
                    CreateNodes(childId, publish, children, depth, currentDepth + 1, sbNamePrefix, sbPath);

                    // Reset for the next child
                    sbNamePrefix.Length = sbNamePrefixLength;
                    sbPath.Length = sbPathLength;
                }
            }

            static void ConfigureDomains(IDatabase database, int rootNodeId, ILanguage[] languages)
            {
                var sb = new StringBuilder();
                int index = 0;
                int count = languages.Length;

                foreach (var language in languages)
                {
                    var isDefault = index == 0; // The default language is always the first one
                    var isLast = ++index == count;

                    sb.Append('(')
                      .Append(language.Id)
                      .Append(", @rootNodeId, '");

                    if (isDefault)
                    {
                        // The default language is at the root, not at /<iso-code>.
                        sb.Append('/');
                    }
                    else
                    {
                        sb.Append('/')
                          .Append(language.IsoCode.ToLowerInvariant());
                    }

                    sb.Append("', ")
                      .Append(index + 1)
                      .Append(')')
                      .Append(isLast ? ";" : ",")
                      .AppendLine();
                }

                var query = $"""
                    INSERT INTO umbracoDomain (domainDefaultLanguage, domainRootStructureID, domainName, sortOrder)
                    VALUES
                    {sb}
                    """;

                var now = DateTimeOffset.UtcNow;
                var args = new
                {
                    rootNodeId
                };

                database.Execute(query, args);
            }
        }

        /// <summary>
        /// Internal Umbraco DTOs required by this migration.
        /// From: \src\Umbraco.Infrastructure\Persistence\Dtos\CLASS_NAME.cs
        /// Commit: 86411e4ae3d28f315cf194ca0070b6479aae1998
        /// </summary>
        private static class UmbracoInternalDtos
        {
            // From: 
            // Commit: 
            [TableName(TableName)]
            [PrimaryKey("id")]
            [ExplicitColumns]
            internal sealed class ContentVersionCultureVariationDto
            {
                public const string TableName = Constants.DatabaseSchema.Tables.ContentVersionCultureVariation;
                private int? _updateUserId;

                [Column("id")]
                [PrimaryKeyColumn]
                public int Id { get; set; }

                [Column("versionId")]
                [ForeignKey(typeof(ContentVersionDto))]
                [Index(IndexTypes.UniqueNonClustered, Name = "IX_" + TableName + "_VersionId", ForColumns = "versionId,languageId", IncludeColumns = "id,name,date,availableUserId")]
                public int VersionId { get; set; }

                [Column("languageId")]
                [ForeignKey(typeof(LanguageDto))]
                [Index(IndexTypes.NonClustered, Name = "IX_" + TableName + "_LanguageId")]
                public int LanguageId { get; set; }

                // this is convenient to carry the culture around, but has no db counterpart
                [Ignore]
                public string? Culture { get; set; }

                [Column("name")]
                public string? Name { get; set; }

                [Column("date")] // TODO: db rename to 'updateDate'
                public DateTime UpdateDate { get; set; }

                [Column("availableUserId")] // TODO: db rename to 'updateDate'
                [ForeignKey(typeof(UserDto))]
                [NullSetting(NullSetting = NullSettings.Null)]
                public int? UpdateUserId
                {
                    get => _updateUserId == 0 ? null : _updateUserId;
                    set => _updateUserId = value;
                } // return null if zero
            }

            [TableName(TableName)]
            [PrimaryKey("id")]
            [ExplicitColumns]
            internal sealed class DocumentCultureVariationDto
            {
                public const string TableName = Constants.DatabaseSchema.Tables.DocumentCultureVariation;

                // Public constants to bind properties between DTOs
                public const string PublishedColumnName = "published";

                [Column("id")]
                [PrimaryKeyColumn]
                public int Id { get; set; }

                [Column("nodeId")]
                [ForeignKey(typeof(NodeDto))]
                [Index(IndexTypes.UniqueNonClustered, Name = "IX_" + TableName + "_NodeId", ForColumns = "nodeId,languageId")]
                public int NodeId { get; set; }

                [Column("languageId")]
                [ForeignKey(typeof(LanguageDto))]
                [Index(IndexTypes.NonClustered, Name = "IX_" + TableName + "_LanguageId")]
                public int LanguageId { get; set; }

                // this is convenient to carry the culture around, but has no db counterpart
                [Ignore]
                public string? Culture { get; set; }

                // authority on whether a culture has been edited
                [Column("edited")]
                public bool Edited { get; set; }

                // de-normalized for perfs
                // (means there is a current content version culture variation for the language)
                [Column("available")]
                public bool Available { get; set; }

                // de-normalized for perfs
                // (means there is a published content version culture variation for the language)
                [Column(PublishedColumnName)]
                public bool Published { get; set; }

                // de-normalized for perfs
                // (when available, copies name from current content version culture variation for the language)
                // (otherwise, it's the published one, 'cos we need to have one)
                [Column("name")]
                [NullSetting(NullSetting = NullSettings.Null)]
                public string? Name { get; set; }
            }

            [TableName(TableName)]
            [PrimaryKey("id")]
            [ExplicitColumns]
            internal sealed class LanguageDto
            {
                public const string TableName = Constants.DatabaseSchema.Tables.Language;

                // Public constants to bind properties between DTOs
                public const string IsoCodeColumnName = "languageISOCode";

                /// <summary>
                ///     Gets or sets the identifier of the language.
                /// </summary>
                [Column("id")]
                [PrimaryKeyColumn(IdentitySeed = 2)]
                public short Id { get; set; }

                /// <summary>
                ///     Gets or sets the ISO code of the language.
                /// </summary>
                [Column(IsoCodeColumnName)]
                [Index(IndexTypes.UniqueNonClustered)]
                [NullSetting(NullSetting = NullSettings.Null)]
                [Length(14)]
                public string? IsoCode { get; set; }

                /// <summary>
                ///     Gets or sets the culture name of the language.
                /// </summary>
                [Column("languageCultureName")]
                [NullSetting(NullSetting = NullSettings.Null)]
                [Length(100)]
                public string? CultureName { get; set; }

                /// <summary>
                ///     Gets or sets a value indicating whether the language is the default language.
                /// </summary>
                [Column("isDefaultVariantLang")]
                [Constraint(Default = "0")]
                public bool IsDefault { get; set; }

                /// <summary>
                ///     Gets or sets a value indicating whether the language is mandatory.
                /// </summary>
                [Column("mandatory")]
                [Constraint(Default = "0")]
                public bool IsMandatory { get; set; }

                /// <summary>
                ///     Gets or sets the identifier of a fallback language.
                /// </summary>
                [Column("fallbackLanguageId")]
                [ForeignKey(typeof(LanguageDto), Column = "id")]
                [Index(IndexTypes.NonClustered)]
                [NullSetting(NullSetting = NullSettings.Null)]
                public int? FallbackLanguageId { get; set; }
            }
        }

        private interface INodeCreator
        {
            /// <summary>
            /// Called before the first node is created.
            /// </summary>
            /// <param name="database">The database</param>
            void Prepare(IDatabase database);

            /// <summary>
            /// Called after the the last node is created.
            /// </summary>
            /// <param name="database">The database</param>
            /// <param name="updateProgress">Delegate to report progress, if any</param>
            void Finish(IDatabase database, UpdateProgress updateProgress);

            /// <summary>
            /// Creates a single node.
            /// </summary>
            /// <param name="database">The database</param>
            /// <param name="name">The node name</param>
            /// <param name="urlSegment">The node URL segment</param>
            /// <param name="parentId">The node parent id</param>
            /// <param name="parentPath">The node parent path</param>
            /// <param name="publish">Whether the node should be published or only saved</param>
            /// <param name="contentTypeId">The content type id</param>
            /// <param name="templateId">The template id</param>
            /// <param name="languages">The languages to create this node for</param>
            /// <param name="sortOrder">The sort order</param>
            /// /// <param name="updateProgress">Delegate to call to update the progress of inserted rows</param>
            /// <returns>The primary key of the created node</returns>
            int CreateNode
            (
                IDatabase database, string name, string urlSegment, int parentId, string parentPath, bool publish,
                int contentTypeId, int templateId, ILanguage[] languages, int sortOrder, UpdateProgress updateProgress
            );
        }

        private class SqliteNodeCreator : INodeCreator
        {
            private int _nodeId;
            private int _contentVersionId;

            public void Prepare(IDatabase database)
            {
                _nodeId = database.ExecuteScalar<int>("SELECT COALESCE(MAX(id), 0) FROM umbracoNode");
                _contentVersionId = database.ExecuteScalar<int>("SELECT COALESCE(MAX(id), 0) FROM umbracoContentVersion");
            }

            public int CreateNode
            (
                IDatabase database, string name, string urlSegment, int parentId, string parentPath, bool publish,
                int contentTypeId, int templateId, ILanguage[] languages, int sortOrder, UpdateProgress updateProgress
            )
            {
                var uniqueId = Guid.NewGuid();
                var level = parentPath.Count(c => c == ',') + 1;
                var nodeId = ++_nodeId;

                // In Umbraco the order is Published, then Draft.
                int? publishedVersionId = publish ? ++_contentVersionId : null;
                int draftVersionId = ++_contentVersionId;

                InsertUmbracoNode(database, nodeId, uniqueId, name, parentId, parentPath, level, sortOrder);
                InsertUmbracoContent(database, nodeId, contentTypeId);
                InsertUmbracoContentVersion(database, nodeId, name, publishedVersionId, draftVersionId);
                InsertUmbracoContentVersionCultureVariation(database, languages, name, publishedVersionId, draftVersionId);
                InsertUmbracoDocument(database, nodeId, publish);
                InsertUmbracoDocumentCultureVariation(database, nodeId, languages, name, publish);
                InsertUmbracoDocumentUrl(database, uniqueId, languages, urlSegment, publish);
                InsertUmbracoDocumentVersion(database, publishedVersionId, draftVersionId, templateId);

                // umbracoNode: 1
                // umbracoContent: 1
                // umbracoContentVersion: publish ? 2 : 1
                // umbracoContentVersionCultureVariation: languages * (publish ? 2 : 1)
                // umbracoDocument: 1
                // umbracoDocumentCultureVariation: languages * 1
                // umbracoDocumentUrl: languages * (publish ? 2 : 1)
                // umbracoDocumentVersion: publish ? 2 : 1
                var insertedRows = publish
                    ? 7 + languages.Length * 5
                    : 5 + languages.Length * 3;

                updateProgress(insertedRows);

                return nodeId;

                static void InsertUmbracoNode(IDatabase database, int nodeId, Guid uniqueId, string nodeName, int parentId, string parentPath, int level, int sortOrder)
                {
                    var query = $"""
                    INSERT INTO umbracoNode(id, uniqueId, parentId, level, path, sortOrder, trashed, nodeUser, text, nodeObjectType, createDate)
                    VALUES
                    (@nodeId, @uniqueId, @parentId, @level, '{parentPath},{nodeId}', @sortOrder, 0, -1, @nodeName, @objectType, @now)
                    RETURNING id;
                    """;

                    var now = DateTimeOffset.UtcNow;
                    var objectType = Constants.ObjectTypes.Document;

                    var args = new
                    {
                        nodeId,
                        uniqueId,
                        parentId,
                        level,
                        sortOrder,
                        nodeName,
                        objectType,
                        now,
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoContent(IDatabase database, int nodeId, int contentTypeId)
                {
                    var query = """
                    INSERT INTO umbracoContent(nodeId, contentTypeId)
                    VALUES
                    (@nodeId, @contentTypeId);
                    """;

                    var args = new
                    {
                        nodeId,
                        contentTypeId
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoContentVersion(IDatabase database, int nodeId, string nodeName, int? publishedVersionId, int draftVersionId)
                {
                    var sb = new StringBuilder();

                    sb.AppendLine("""
                        INSERT INTO umbracoContentVersion (id, nodeId, versionDate, userId, current, text, preventCleanup)
                        VALUES
                        """);

                    // If published, insert non-current version
                    if (publishedVersionId.HasValue)
                    {
                        sb.Append('(').Append(publishedVersionId.Value).AppendLine(", @nodeId, @now, -1, 0, @nodeName, 0),");
                    }

                    // Always insert draft (current = 1)
                    sb.Append('(').Append(draftVersionId).AppendLine(", @nodeId, @now, -1, 1, @nodeName, 0);");

                    var query = sb.ToString();

                    var now = DateTimeOffset.UtcNow;
                    var objectType = Constants.ObjectTypes.Document;

                    var args = new
                    {
                        nodeId,
                        now,
                        nodeName,
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoContentVersionCultureVariation(IDatabase database, ILanguage[] languages, string nodeName, int? publishedVersionId, int draftVersionId)
                {
                    var sb = new StringBuilder();
                    int index = 0;

                    foreach (var language in languages)
                    {
                        bool last = ++index == languages.Length;

                        if (publishedVersionId.HasValue)
                        {
                            // Published
                            sb.Append('(')
                              .Append(publishedVersionId.Value).Append(", ")
                              .Append(language.Id).Append(", ")
                              .AppendLine("@nodeName, @now, NULL),");
                        }

                        // Draft
                        sb.Append('(')
                          .Append(draftVersionId).Append(", ")
                          .Append(language.Id).Append(", ")
                          .Append("@nodeName, @now, NULL)")
                          .AppendLine(last ? ";" : ",");
                    }

                    var query = $"""
                    INSERT INTO umbracoContentVersionCultureVariation (versionId, languageId, name, date, availableUserId)
                    VALUES
                    {sb}
                    """;

                    var now = DateTimeOffset.UtcNow;
                    var args = new
                    {
                        nodeName,
                        now
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoDocument(IDatabase database, int nodeId, bool isPublished)
                {
                    var query = """
                    INSERT INTO umbracoDocument(nodeId, published, edited)
                    VALUES
                    (@nodeId, @published, @edited);
                    """;

                    var args = new
                    {
                        nodeId,
                        published = isPublished,
                        edited = !isPublished
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoDocumentCultureVariation(IDatabase database, int nodeId, ILanguage[] languages, string nodeName, bool isPublished)
                {
                    var sb = new StringBuilder();
                    int index = 0;
                    int count = languages.Length;

                    foreach (var language in languages)
                    {
                        bool last = ++index == count;
                        sb.Append("(@nodeId, ")
                          .Append(language.Id)
                          .Append(", @edited, 1, @published, @nodeName)")
                          .Append(last ? ";" : ",")
                          .AppendLine();
                    }

                    var query = $"""
                    INSERT INTO umbracoDocumentCultureVariation (nodeId, languageId, edited, available, published, name)
                    VALUES
                    {sb}
                    """;

                    var now = DateTimeOffset.UtcNow;
                    var args = new
                    {
                        nodeId,
                        nodeName,
                        edited = !isPublished,
                        published = isPublished,
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoDocumentUrl(IDatabase database, Guid uniqueId, ILanguage[] languages, string nodeSegment, bool isPublished)
                {
                    var sb = new StringBuilder();
                    int index = 0;

                    foreach (var language in languages)
                    {
                        bool last = ++index == languages.Length;

                        if (isPublished)
                        {
                            // Published
                            sb.Append("(@uniqueId, 0, ") // isDraft = 0
                              .Append(language.Id)
                              .AppendLine(", @nodeSegment, 1),");
                        }

                        // Draft
                        sb.Append("(@uniqueId, 1, ") // isDraft = 1
                          .Append(language.Id)
                          .Append(", @nodeSegment, 1)")
                          .AppendLine(last ? ";" : ",");
                    }

                    var query = $"""
                    INSERT INTO umbracoDocumentUrl (uniqueId, isDraft, languageId, urlSegment, isPrimary)
                    VALUES
                    {sb}
                    """;

                    var now = DateTimeOffset.UtcNow;
                    var args = new
                    {
                        uniqueId,
                        nodeSegment
                    };

                    database.Execute(query, args);
                }

                static void InsertUmbracoDocumentVersion(IDatabase database, int? publishedVersionId, int draftVersionId, int templateId)
                {
                    var sb = new StringBuilder();

                    if (publishedVersionId.HasValue)
                    {
                        // Published
                        sb.Append('(')
                          .Append(publishedVersionId)
                          .AppendLine(", @templateId, 1),"); // published = 1
                    }

                    // Draft
                    sb.Append('(')
                      .Append(draftVersionId)
                      .AppendLine(", @templateId, 0);"); // published = 0

                    var query = $"""
                    INSERT INTO umbracoDocumentVersion (id, templateId, published)
                    VALUES
                    {sb}
                    """;

                    var args = new
                    {
                        templateId
                    };

                    database.Execute(query, args);
                }
            }

            public void Finish(IDatabase _, UpdateProgress __)
            {
                // Nothing
            }
        }

        private class SqlServerNodeCreator : INodeCreator
        {
            private readonly List<NodeDto> _nodeDtos = [];
            private readonly List<ContentDto> _contentDtos = [];
            private readonly List<ContentVersionDto> _contentVersionDtos = [];
            private readonly List<UmbracoInternalDtos.ContentVersionCultureVariationDto> _contentVersionContentVariationDtos = [];
            private readonly List<DocumentDto> _documentDtos = [];
            private readonly List<UmbracoInternalDtos.DocumentCultureVariationDto> _documentCultureVariationDtos = [];
            private readonly List<DocumentUrlDto> _documentUrlDtos = [];
            private readonly List<DocumentVersionDto> _documentVersionDtos = [];

            private int _nodeId;
            private int _contentVersionId;

            public void Prepare(IDatabase database)
            {
                // In case this instance will be used for multiple sessions we reset the instance data.
                _nodeDtos.Clear();
                _contentDtos.Clear();
                _contentVersionDtos.Clear();
                _contentVersionContentVariationDtos.Clear();
                _documentDtos.Clear();
                _documentCultureVariationDtos.Clear();
                _documentUrlDtos.Clear();
                _documentVersionDtos.Clear();

                _nodeId = database.ExecuteScalar<int>("SELECT COALESCE(MAX(id), 0) FROM umbracoNode");
                _contentVersionId = database.ExecuteScalar<int>("SELECT COALESCE(MAX(id), 0) FROM umbracoContentVersion");
            }

            public int CreateNode
            (
                IDatabase database, string name, string urlSegment, int parentId, string parentPath, bool publish,
                int contentTypeId, int templateId, ILanguage[] languages, int sortOrder, UpdateProgress updateProgress
            )
            {
                var nodeId = ++_nodeId; // Advance the primary key
                var uniqueId = Guid.NewGuid();
                var level = parentPath.Count(c => c == ',') + 1;

                // In Umbraco the order is Published, then Draft.
                int? publishedVersionId = publish ? ++_contentVersionId : null;
                int draftVersionId = ++_contentVersionId;

                // Prepare for bulk inserts later
                PrepareUmbracoNode(database, nodeId, uniqueId, name, parentId, parentPath, level, sortOrder, _nodeDtos);
                PrepareUmbracoContent(database, nodeId, contentTypeId, _contentDtos);
                PrepareUmbracoContentVersion(database, nodeId, name, publishedVersionId, draftVersionId, _contentVersionDtos);
                PrepareUmbracoContentVersionCultureVariation(database, languages, name, publishedVersionId, draftVersionId, _contentVersionContentVariationDtos);
                PrepareUmbracoDocument(database, nodeId, publish, _documentDtos);
                PrepareUmbracoDocumentCultureVariation(database, nodeId, languages, name, publish, _documentCultureVariationDtos);
                PrepareUmbracoDocumentUrl(database, uniqueId, languages, urlSegment, publish, _documentUrlDtos);
                PrepareUmbracoDocumentVersion(database, publishedVersionId, draftVersionId, templateId, _documentVersionDtos);

                return nodeId;

                static void PrepareUmbracoNode(
                    IDatabase database, int nodeId, Guid uniqueId, string nodeName,
                    int parentId, string parentPath, int level, int sortOrder, List<NodeDto> dtos)
                {
                    dtos.Add(new NodeDto
                    {
                        NodeId = nodeId,
                        UniqueId = uniqueId,
                        ParentId = parentId,
                        Level = (short)level,
                        SortOrder = sortOrder,
                        Path = $"{parentPath},{nodeId}",
                        Text = nodeName,
                        NodeObjectType = Constants.ObjectTypes.Document,
                        CreateDate = DateTime.UtcNow,
                        Trashed = false,
                        UserId = -1,
                    });
                }

                static void PrepareUmbracoContent(IDatabase database, int nodeId, int contentTypeId, List<ContentDto> dtos)
                {
                    dtos.Add(new ContentDto
                    {
                        NodeId = nodeId,
                        ContentTypeId = contentTypeId,
                    });
                }

                static void PrepareUmbracoContentVersion(
                    IDatabase database, int nodeId, string nodeName,
                    int? publishedVersionId, int draftVersionId, List<ContentVersionDto> dtos)
                {
                    if (publishedVersionId.HasValue)
                    {
                        // Published
                        dtos.Add(new ContentVersionDto
                        {
                            Id = publishedVersionId.Value,
                            NodeId = nodeId,
                            VersionDate = DateTime.UtcNow,
                            UserId = -1,
                            Current = false,
                            Text = nodeName,
                            PreventCleanup = false,
                        });
                    }

                    // Draft
                    dtos.Add(new ContentVersionDto
                    {
                        Id = draftVersionId,
                        NodeId = nodeId,
                        VersionDate = DateTime.UtcNow,
                        UserId = -1,
                        Current = true,
                        Text = nodeName,
                        PreventCleanup = false,
                    });
                }

                static void PrepareUmbracoContentVersionCultureVariation(
                    IDatabase database, ILanguage[] languages, string nodeName,
                    int? publishedVersionId, int draftVersionId, List<UmbracoInternalDtos.ContentVersionCultureVariationDto> dtos)
                {
                    foreach (var language in languages)
                    {
                        if (publishedVersionId.HasValue)
                        {
                            dtos.Add(new UmbracoInternalDtos.ContentVersionCultureVariationDto
                            {
                                VersionId = publishedVersionId.Value,
                                LanguageId = language.Id,
                                Name = nodeName,
                                UpdateDate = DateTime.UtcNow,
                                UpdateUserId = -1,
                            });
                        }

                        dtos.Add(new UmbracoInternalDtos.ContentVersionCultureVariationDto
                        {
                            VersionId = draftVersionId,
                            LanguageId = language.Id,
                            Name = nodeName,
                            UpdateDate = DateTime.UtcNow,
                            UpdateUserId = -1,
                        });
                    }
                }

                static void PrepareUmbracoDocument(IDatabase database, int nodeId, bool isPublished, List<DocumentDto> dtos)
                {
                    dtos.Add(new DocumentDto
                    {
                        NodeId = nodeId,
                        Published = isPublished,
                        Edited = !isPublished,
                    });
                }

                static void PrepareUmbracoDocumentCultureVariation(IDatabase database, int nodeId, ILanguage[] languages, string nodeName, bool isPublished, List<UmbracoInternalDtos.DocumentCultureVariationDto> dtos)
                {
                    foreach (var language in languages)
                    {
                        dtos.Add(new UmbracoInternalDtos.DocumentCultureVariationDto
                        {
                            NodeId = nodeId,
                            LanguageId = language.Id,
                            Edited = !isPublished,
                            Available = true,
                            Published = isPublished,
                            Name = nodeName,
                        });
                    }
                }

                static void PrepareUmbracoDocumentUrl(IDatabase database, Guid uniqueId, ILanguage[] languages, string nodeSegment, bool isPublished, List<DocumentUrlDto> dtos)
                {
                    foreach (var language in languages)
                    {
                        if (isPublished)
                        {
                            // Published
                            dtos.Add(new DocumentUrlDto
                            {
                                UniqueId = uniqueId,
                                IsDraft = false,
                                LanguageId = language.Id,
                                UrlSegment = nodeSegment,
                                IsPrimary = true,
                            });
                        }

                        // Draft
                        dtos.Add(new DocumentUrlDto
                        {
                            UniqueId = uniqueId,
                            IsDraft = true,
                            LanguageId = language.Id,
                            UrlSegment = nodeSegment,
                            IsPrimary = true,
                        });
                    }
                }

                static void PrepareUmbracoDocumentVersion(IDatabase database, int? publishedVersionId, int draftVersionId, int templateId, List<DocumentVersionDto> dtos)
                {
                    if (publishedVersionId.HasValue)
                    {
                        // Published
                        dtos.Add(new DocumentVersionDto
                        {
                            Id = publishedVersionId.Value,
                            TemplateId = templateId,
                            Published = true,
                        });
                    }

                    // Draft
                    dtos.Add(new DocumentVersionDto
                    {
                        Id = draftVersionId,
                        TemplateId = templateId,
                        Published = false,
                    });
                }
            }

            public void Finish(IDatabase database, UpdateProgress updateProgress)
            {
                SqlBulkCopyOptions bulkCopyOptions = SqlBulkCopyOptions.TableLock;

                SqlBulkCopyHelper.BulkInsert(database, _nodeDtos, bulkCopyOptions, null);
                updateProgress(_nodeDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _contentDtos, bulkCopyOptions, null);
                updateProgress(_contentDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _contentVersionDtos, bulkCopyOptions, null);
                updateProgress(_contentVersionDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _contentVersionContentVariationDtos, bulkCopyOptions, null);
                updateProgress(_contentVersionContentVariationDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _documentDtos, bulkCopyOptions, null);
                updateProgress(_documentDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _documentCultureVariationDtos, bulkCopyOptions, null);
                updateProgress(_documentCultureVariationDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _documentUrlDtos, bulkCopyOptions, null);
                updateProgress(_documentUrlDtos.Count);

                SqlBulkCopyHelper.BulkInsert(database, _documentVersionDtos, bulkCopyOptions, null);
                updateProgress(_documentVersionDtos.Count);
            }
        }

        /// <summary>
        /// Represents a callback method that reports the number of rows that have been inserted during a data
        /// operation. To be called whenever the number of inserted rows changes. Call with the delta of inserted rows
        /// since the previous invocation.
        /// </summary>
        /// <param name="insertedRows">The number of rows that have been successfully inserted since the last invocation. Must be zero or greater.</param>
        private delegate void UpdateProgress(int insertedRows);
    }

    private static class ConfigReader
    {
        internal static int GetRequiredInt(IConfiguration config, string key)
        {
            var value = config.GetValue<int>(key);

            if (value <= 0)
            {
                throw new InvalidOperationException($"'{key}' must be at least 1");
            }

            return value;
        }
    }
}
