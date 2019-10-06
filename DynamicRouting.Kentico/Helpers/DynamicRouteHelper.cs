﻿using CMS.Base;
using CMS.DataEngine;
using CMS.DocumentEngine;
using CMS.Helpers;
using CMS.Localization;
using CMS.MacroEngine;
using CMS.SiteProvider;
using DynamicRouting.Helpers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Serialization;

namespace DynamicRouting
{
    /// <summary>
    /// Helper methods that execute the checks
    /// </summary>
    public static class DynamicRouteHelper
    {

        /// <summary>
        /// Gets the CMS Page using Dynamic Routing, returning the culture variation that either matches the given culture or the Slug's culture, or the default site culture if not found.
        /// </summary>
        /// <param name="Url">The Url (part after the domain), if empty will use the Current Request</param>
        /// <param name="Culture">The Culture, not needed if the Url contains the culture that the UrlSlug has as part of it's generation.</param>
        /// <param name="SiteName">The Site Name, defaults to current site.</param>
        /// <returns>The Page that matches the Url Slug, for the given or matching culture (or default culture if one isn't found).</returns>
        public static TreeNode GetPage(string Url = "", string Culture = "", string SiteName = "")
        {
            // Load defaults
            SiteName = (!string.IsNullOrWhiteSpace(SiteName) ? SiteName : SiteContext.CurrentSiteName);
            string DefaultCulture = SiteContext.CurrentSite.DefaultVisitorCulture;
            if(!string.IsNullOrWhiteSpace(Url))
            {
                Url = EnvironmentHelper.GetUrl(HttpContext.Current.Request.Url.AbsolutePath, HttpContext.Current.Request.ApplicationPath);
            }

            // Get Page based on Url
            return DocumentHelper.GetDocuments()
                .Source(x => x.Join<UrlSlugInfo>("NodeID", "NodeID", JoinTypeEnum.LeftOuter))
                .Source(x => x.Join<SiteInfo>("NodeSiteID", "SiteID", JoinTypeEnum.Inner))
                .WhereEquals("UrlSlug", Url)
                .WhereEquals("SiteName", SiteName)
                .OrderBy(string.Format("case when CultureCode = '{0}' then 0 else 1", SqlHelper.EscapeQuotes(Culture)))
                .OrderBy(string.Format("case when CultureCode = '{0}' then 0 else 1", SqlHelper.EscapeQuotes(DefaultCulture)))
                .FirstOrDefault();
        }

        /// <summary>
        /// If the Site name is empty or null, returns the current site context's site name
        /// </summary>
        /// <param name="SiteName">The Site Name</param>
        /// <returns>The Site Name if given, or the current site name</returns>
        private static string GetSiteName(string SiteName)
        {
            return !string.IsNullOrWhiteSpace(SiteName) ? SiteName : SiteContext.CurrentSiteName;
        }

        #region "Relational Checks required"

        /// <summary>
        /// Check if any pages of children of this node's parent have a class with Url Pattern contains NodeOrder, if so then the siblings of a changed node need to have its siblings built (start with parent)
        /// </summary>
        /// <returns>If Siblings need to be checked for URL slug changes</returns>
        public static bool CheckSiblings(string NodeAliasPath = "", string SiteName = "")
        {
            // Get any classes that have {% NodeOrder %}
            List<int> ClassIDs = CacheHelper.Cache<List<int>>(cs =>
            {
                if (cs.Cached)
                {
                    cs.CacheDependency = CacheHelper.GetCacheDependency("cms.class|all");
                }
                return DataClassInfoProvider.GetClasses()
                .WhereLike("ClassURLPattern", "%NodeOrder%")
                .Select(x => x.ClassID).ToList();
            }, new CacheSettings(1440, "ClassesForSiblingCheck"));

            // If no NodeAliasPath given, then return if there are any Classes that have NodeOrder
            if (string.IsNullOrWhiteSpace(NodeAliasPath))
            {
                return ClassIDs.Count > 0;
            }
            else
            {
                SiteName = GetSiteName(SiteName);

                var Document = DocumentHelper.GetDocument(new NodeSelectionParameters()
                {
                    SelectSingleNode = true,
                    AliasPath = NodeAliasPath,
                    SiteName = SiteName
                }, new TreeProvider());

                // return if any siblings have a class NodeOrder exist
                return DocumentHelper.GetDocuments()
                    .WhereEquals("NodeParentID", Document.NodeParentID)
                    .WhereNotEquals("NodeID", Document.NodeID)
                    .WhereIn("NodeClassID", ClassIDs)
                    .Columns("NodeID")
                    .Count > 0;
            }
        }

        /// <summary>
        /// Check if any child Url Pattern contains NodeLevel, NodeParentID, NodeAliasPath, DocumentNamePath if so then must check all children as there could be one of these page types down the tree that is modified.
        /// </summary>
        /// <returns>If Descendents need to be checked for URL slug changes</returns>
        public static bool CheckDescendents(string NodeAliasPath = "", string SiteName = "")
        {
            // Get any classes that have {% NodeOrder %}
            List<int> ClassIDs = CacheHelper.Cache<List<int>>(cs =>
            {
                if (cs.Cached)
                {
                    cs.CacheDependency = CacheHelper.GetCacheDependency("cms.class|all");
                }
                return DataClassInfoProvider.GetClasses()
                .WhereLike("ClassURLPattern", "%NodeLevel%")
                .Or()
                .WhereLike("ClassURLPattern", "%NodeParentID%")
                .Or()
                .WhereLike("ClassURLPattern", "%NodeAliasPath%")
                .Or()
                .WhereLike("ClassURLPattern", "%DocumentNamePath%")
                .Select(x => x.ClassID).ToList();
            }, new CacheSettings(1440, "ClassesForDescendentCheck"));

            // If no NodeAliasPath given, then return if there are any Classes that have NodeOrder
            if (string.IsNullOrWhiteSpace(NodeAliasPath))
            {
                return ClassIDs.Count > 0;
            }
            else
            {
                SiteName = GetSiteName(SiteName);

                // return if any siblings have a class NodeOrder exist
                return DocumentHelper.GetDocuments()
                    .Path(NodeAliasPath, PathTypeEnum.Children)
                    .OnSite(new SiteInfoIdentifier(SiteName))
                    .WhereIn("NodeClassID", ClassIDs)
                    .Columns("NodeID")
                    .Count > 0;
            }
        }

        /// <summary>
        /// Check if any Url pattern contains ParentUrl
        /// </summary>
        /// <returns>If Children need to be checked for URL slug changes</returns>
        public static bool CheckChildren(string NodeAliasPath = "", string SiteName = "")
        {
            // Get any classes that have {% NodeOrder %}
            List<int> ClassIDs = CacheHelper.Cache<List<int>>(cs =>
            {
                if (cs.Cached)
                {
                    cs.CacheDependency = CacheHelper.GetCacheDependency("cms.class|all");
                }
                return DataClassInfoProvider.GetClasses()
                .WhereLike("ClassURLPattern", "%ParentUrl%")
                .Select(x => x.ClassID).ToList();
            }, new CacheSettings(1440, "ClassesForChildrenCheck"));

            // If no NodeAliasPath given, then return if there are any Classes that have NodeOrder
            if (string.IsNullOrWhiteSpace(NodeAliasPath))
            {
                return ClassIDs.Count > 0;
            }
            else
            {
                SiteName = GetSiteName(SiteName);

                var Document = DocumentHelper.GetDocument(new NodeSelectionParameters()
                {
                    SelectSingleNode = true,
                    AliasPath = NodeAliasPath,
                    SiteName = SiteName
                }, new TreeProvider());

                // return if any Children have a class NodeOrder exist
                return DocumentHelper.GetDocuments()
                    .WhereEquals("NodeParentID", Document.NodeID)
                    .WhereIn("NodeClassID", ClassIDs)
                    .Columns("NodeID")
                    .Count > 0;
            }
        }

        #endregion

        /// <summary>
        /// Get the Node Item Builder Settings based on the given NodeAliasPath, should be used for individual page updates as will limit what is checked based on the page.
        /// </summary>
        /// <param name="NodeAliasPath">The Node Alias Path</param>
        /// <param name="SiteName"></param>
        /// <param name="CheckingForUpdates"></param>
        /// <param name="CheckEntireTree"></param>
        /// <returns></returns>
        private static NodeItemBuilderSettings GetNodeItemBuilderSettings(string NodeAliasPath, string SiteName, bool CheckingForUpdates, bool CheckEntireTree)
        {
            return CacheHelper.Cache(cs =>
            {
                if (cs.Cached)
                {
                    cs.CacheDependency = CacheHelper.GetCacheDependency(new string[]
                    {
                        "cms.site|byname|"+SiteName,
                        "cms.siteculture|all",
                        "cms.settingskey|byname|CMSDefaultCultureCode",
                        "cms.settingskey|byname|GenerateCultureVariationUrlSlugs",
                        "cms.class|all"
                    });
                }
                // Loop through Cultures and start rebuilding pages
                SiteInfo Site = SiteInfoProvider.GetSiteInfo(SiteName);
                string DefaultCulture = SettingsKeyInfoProvider.GetValue("CMSDefaultCultureCode", new SiteInfoIdentifier(SiteName));

                bool GenerateCultureVariationUrlSlugs = SettingsKeyInfoProvider.GetBoolValue("GenerateCultureVariationUrlSlugs", new SiteInfoIdentifier(SiteName));

                var BaseMacroResolver = MacroResolver.GetInstance(true);
                BaseMacroResolver.AddAnonymousSourceData(new object[] { Site });

                // Now build URL slugs for the default language always.
                List<string> Cultures = CultureSiteInfoProvider.GetSiteCultureCodes(SiteName);

                // Configure relational checks based on node
                bool BuildSiblings = CheckSiblings(NodeAliasPath, SiteName);
                bool BuildDescendents = CheckDescendents(NodeAliasPath, SiteName);
                bool BuildChildren = CheckChildren(NodeAliasPath, SiteName);

                return new NodeItemBuilderSettings(Cultures, DefaultCulture, GenerateCultureVariationUrlSlugs, BaseMacroResolver, CheckingForUpdates, CheckEntireTree, BuildSiblings, BuildChildren, BuildDescendents, SiteName);
            }, new CacheSettings(1440, "GetNodeItemBuilderSettings", NodeAliasPath, SiteName, CheckingForUpdates, CheckEntireTree));
        }

        /// <summary>
        /// Helper that fills in the NodeItemBuilderSetting based on the SiteName and configured options.
        /// </summary>
        /// <param name="SiteName">The Site Name</param>
        /// <param name="CheckingForUpdates">If Updates should be checked or not, triggering recursive checking</param>
        /// <param name="CheckEntireTree">If the entire tree should be checked</param>
        /// <param name="BuildSiblings">If siblings should be checked</param>
        /// <param name="BuildChildren">If Children Should be checked</param>
        /// <param name="BuildDescendents">If Descdendents should be checked</param>
        /// <returns>The NodeItemBuilderSetting with Site, Cultures and Default culture already set.</returns>
        private static NodeItemBuilderSettings GetNodeItemBuilderSettings(string SiteName, bool CheckingForUpdates, bool CheckEntireTree, bool BuildSiblings, bool BuildChildren, bool BuildDescendents)
        {
            return CacheHelper.Cache(cs =>
            {
                if (cs.Cached)
                {
                    cs.CacheDependency = CacheHelper.GetCacheDependency(new string[]
                    {
                        "cms.site|byname|"+SiteName,
                        "cms.siteculture|all",
                        "cms.settingskey|byname|CMSDefaultCultureCode",
                        "cms.settingskey|byname|GenerateCultureVariationUrlSlugs",
                    });
                }
                // Loop through Cultures and start rebuilding pages
                SiteInfo Site = SiteInfoProvider.GetSiteInfo(SiteName);
                string DefaultCulture = SettingsKeyInfoProvider.GetValue("CMSDefaultCultureCode", new SiteInfoIdentifier(SiteName));

                bool GenerateCultureVariationUrlSlugs = SettingsKeyInfoProvider.GetBoolValue("GenerateCultureVariationUrlSlugs", new SiteInfoIdentifier(SiteName));

                var BaseMacroResolver = MacroResolver.GetInstance(true);
                BaseMacroResolver.AddAnonymousSourceData(new object[] { Site });

                // Now build URL slugs for the default language always.
                List<string> Cultures = CultureSiteInfoProvider.GetSiteCultureCodes(SiteName);

                return new NodeItemBuilderSettings(Cultures, DefaultCulture, GenerateCultureVariationUrlSlugs, BaseMacroResolver, CheckingForUpdates, CheckEntireTree, BuildSiblings, BuildChildren, BuildDescendents, SiteName);
            }, new CacheSettings(1440, "GetNodeItemBuilderSettings", SiteName, CheckingForUpdates, CheckEntireTree, BuildSiblings, BuildChildren, BuildDescendents));
        }

        /// <summary>
        /// Rebuilds all URL Routes on all sites.
        /// </summary>
        public static void RebuildRoutes()
        {
            foreach (SiteInfo Site in SiteInfoProvider.GetSites())
            {
                RebuildRoutesBySite(Site.SiteName);
            }
        }

        /// <summary>
        /// Rebuilds all URL Routes on the given Site
        /// </summary>
        /// <param name="SiteName">The Site name</param>
        public static void RebuildRoutesBySite(string SiteName)
        {
            // Get NodeItemBuilderSettings
            NodeItemBuilderSettings BuilderSettings = GetNodeItemBuilderSettings(SiteName, true, true, true, true, true);

            // Get root NodeID
            TreeNode RootNode = DocumentHelper.GetDocument(new NodeSelectionParameters()
            {
                AliasPath = "/",
                SiteName = SiteName
            }, new TreeProvider());

            // Rebuild NodeItem tree structure, this will only affect the initial node syncly.
            NodeItem RootNodeItem = new NodeItem(RootNode.NodeID, BuilderSettings);
            if (ErrorOnConflict())
            {
                // If error on conflict, everything MUST be done syncly.
                RootNodeItem.BuildChildren();
                if (RootNodeItem.ConflictsExist())
                {
                    throw new Exception("Conflict Exists, aborting save");
                }
                // Save changes
                RootNodeItem.SaveChanges();
            } else
            {
                // Do rest asyncly.
                QueueUpUrlSlugGeneration(RootNodeItem);
            }
        }

        private static void QueueUpUrlSlugGeneration(NodeItem NodeItem)
        {
            // Add item to the Slug Generation Queue
            SlugGenerationQueueInfo NewQueue = new SlugGenerationQueueInfo()
            {
                SlugGenerationQueueNodeItem = SerializeObject<NodeItem>(NodeItem)
            };
            SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(NewQueue);
            
            // Run Queue checker
            CheckUrlSlugGenerationQueue();
        }

        /// <summary>
        /// Clears the QueueRUnning flag on any tasks that the thread doesn't actually exist (something happened and the thread failed or died without setting the Running to false
        /// </summary>
        public static void ClearStuckUrlGenerationTasks()
        {
            Process currentProcess = Process.GetCurrentProcess();

            // Set any threads by this application that shows it's running but the Thread doesn't actually exist.
            SlugGenerationQueueInfoProvider.GetSlugGenerationQueues()
                .WhereEquals("SlugGenerationQueueRunning", true)
                .WhereEquals("SlugGenerationQueueApplicationID", SystemHelper.ApplicationIdentifier)
                .WhereNotIn("SlugGenerationQueueThreadID", currentProcess.Threads.Cast<ProcessThread>().Select(x => x.Id).ToArray())
                .ForEachObject(x =>
                {
                    x.SlugGenerationQueueRunning = false;
                    SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(x);
                });
        }

        /// <summary>
        /// Checks for Url Slug Generation Queue Items and processes any asyncly.
        /// </summary>
        public static void CheckUrlSlugGenerationQueue()
        {
            // Clear any stuck tasks
            ClearStuckUrlGenerationTasks();

            DataSet NextGenerationResult = ConnectionHelper.ExecuteQuery("DynamicRouting.SlugGenerationQueue.GetNextRunnableQueueItem", new QueryDataParameters()
            {
                {"@ApplicationID", SystemHelper.ApplicationIdentifier }
                ,{"@SkipErroredGenerations", SkipErroredGenerations()}
            });

            if(NextGenerationResult.Tables[0].Rows.Count > 0)
            {
                // Queue up task asyncly
                CMSThread UrlGenerationThread = new CMSThread(new ThreadStart(RunSlugGenerationQueueItem), new ThreadSettings()
                {
                    Mode = ThreadModeEnum.Async,
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal,
                    UseEmptyContext = false,
                    CreateLog = true
                });
                UrlGenerationThread.Start();
            }
        }

        /// <summary>
        /// Async helper method, grabs the Queue that is set to be "running" for this application and processes.
        /// </summary>
        private static void RunSlugGenerationQueueItem()
        {
            // Get the current thread ID and the item to run
            SlugGenerationQueueInfo ItemToRun = SlugGenerationQueueInfoProvider.GetSlugGenerationQueues()
                .WhereEquals("SlugGenerationQueueRunning", 1)
                .WhereEquals("SlugGenerationQueueApplicationID", SystemHelper.ApplicationIdentifier)
                .FirstOrDefault();
            if(ItemToRun == null)
            {
                return;
            }

            // Update item with thread and times
            ItemToRun.SlugGenerationQueueThreadID = Thread.CurrentThread.ManagedThreadId;
            ItemToRun.SlugGenerationQueueStarted = DateTime.Now;
            ItemToRun.SetValue("SlugGenerationQueueErrors", null);
            ItemToRun.SetValue("SlugGenerationQueueEnded", null);
            SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(ItemToRun);

            // Get the NodeItem from the SlugGenerationQueueItem
            var serializer = new XmlSerializer(typeof(NodeItem));
            NodeItem QueueItem;
            using (TextReader reader = new StringReader(ItemToRun.SlugGenerationQueueNodeItem))
            {
                QueueItem = (NodeItem)serializer.Deserialize(reader);
            }

            // Build and Save Items
            try
            {
                QueueItem.BuildChildren();
                QueueItem.SaveChanges();
            }catch(Exception ex)
            {
                // To do, perhaps create better error like those found in the event log or staging task erorrs?
                ItemToRun.SlugGenerationQueueErrors = ex.Message;
                ItemToRun.SlugGenerationQueueRunning = false;
                ItemToRun.SlugGenerationQueueEnded = DateTime.Now;
                SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(ItemToRun);
            }
            // If completed successfully, delete the item
            ItemToRun.Delete();

            // Now that we are 'finished' call the Check again to processes next item.
            CheckUrlSlugGenerationQueue();
        }

        /// <summary>
        /// Runs the Generation on the given Slug Generation Queue, runs regardless of whether or not any other queues are running.
        /// </summary>
        /// <param name="SlugGenerationQueueID"></param>
        public static void RunSlugGenerationQueueItem(int SlugGenerationQueueID)
        {
            SlugGenerationQueueInfo ItemToRun = SlugGenerationQueueInfoProvider.GetSlugGenerationQueues()
                .WhereEquals("SlugGenerationQueueID", SlugGenerationQueueID)
                .FirstOrDefault();
            if (ItemToRun == null)
            {
                return;
            }

            // Update item with thread and times
            ItemToRun.SlugGenerationQueueThreadID = Thread.CurrentThread.ManagedThreadId;
            ItemToRun.SlugGenerationQueueStarted = DateTime.Now;
            ItemToRun.SlugGenerationQueueRunning = true;
            ItemToRun.SlugGenerationQueueApplicationID = SystemHelper.ApplicationIdentifier;
            ItemToRun.SetValue("SlugGenerationQueueErrors", null);
            ItemToRun.SetValue("SlugGenerationQueueEnded", null);
            SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(ItemToRun);

            // Get the NodeItem from the SlugGenerationQueueItem
            var serializer = new XmlSerializer(typeof(NodeItem));
            NodeItem QueueItem;
            using (TextReader reader = new StringReader(ItemToRun.SlugGenerationQueueNodeItem))
            {
                QueueItem = (NodeItem)serializer.Deserialize(reader);
            }

            // Build and Save Items
            try
            {
                QueueItem.BuildChildren();
                QueueItem.SaveChanges();
            }
            catch (Exception ex)
            {
                // To do, perhaps create better error like those found in the event log or staging task erorrs?
                ItemToRun.SlugGenerationQueueErrors = ex.Message;
                ItemToRun.SlugGenerationQueueRunning = false;
                ItemToRun.SlugGenerationQueueEnded = DateTime.Now;
                SlugGenerationQueueInfoProvider.SetSlugGenerationQueueInfo(ItemToRun);
            }
            // If completed successfully, delete the item
            ItemToRun.Delete();
        }

        /// <summary>
        /// If true, then other queued Url Slug Generation tasks will be executed even if one has an error.  Risk is that you could end up with a change waiting to processes that was altered by a later task, thus reverting the true value possibly.
        /// </summary>
        /// <returns></returns>
        private static bool SkipErroredGenerations()
        {
            // TODO: Get from setting
            return false;
        }

        private static string SerializeObject<T>(this T toSerialize)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(toSerialize.GetType());

            using (StringWriter textWriter = new StringWriter())
            {
                xmlSerializer.Serialize(textWriter, toSerialize);
                return textWriter.ToString();
            }
        }

        /// <summary>
        /// Rebuilds all URL Routes for nodes which use this class across all sites.
        /// </summary>
        /// <param name="ClassName">The Class Name</param>
        public static void RebuildRoutesByClass(string ClassName)
        {
            DataClassInfo Class = GetClass(ClassName);
            foreach (string SiteName in SiteInfoProvider.GetSites().Select(x => x.SiteName))
            {
                // Get NodeItemBuilderSettings
                NodeItemBuilderSettings BuilderSettings = GetNodeItemBuilderSettings(SiteName, true, true, true, true, true);

                // Build all, gather nodes of any Node that is of this type of class, check for updates.
                List<int> NodeIDs = DocumentHelper.GetDocuments()
                    .WhereEquals("NodeClassID", Class.ClassID)
                    .OnSite(new SiteInfoIdentifier(SiteName))
                    .CombineWithDefaultCulture()
                    .Distinct()
                    .Columns("NodeID")
                    .OrderBy("NodeLevel, NodeOrder")
                    .Select(x => x.NodeID)
                    .ToList();

                // Check all parent nodes for changes
                foreach (int NodeID in NodeIDs)
                {
                    RebuildRoutesByNode(NodeID, BuilderSettings);
                }
            }
        }

        /// <summary>
        /// Rebuilds the routes for the given Node
        /// </summary>
        /// <param name="NodeID">The NodeID</param>
        public static void RebuildRoutesByNode(int NodeID)
        {
            RebuildRoutesByNode(NodeID, null);
        }

        /// <summary>
        /// Rebuilds the Routes for the given Node, optionally allows for settings to be passed
        /// </summary>
        /// <param name="NodeID">The NodeID</param>
        /// <param name="Settings">The Node Item Build Settings, if null will create settings based on the Node itself.</param>
        private static void RebuildRoutesByNode(int NodeID, NodeItemBuilderSettings Settings = null)
        {
            // If settings are not set, then get settings based on the given Node
            if (Settings == null)
            {
                // Get Site from Node
                TreeNode Page = DocumentHelper.GetDocuments()
                    .WhereEquals("NodeID", NodeID)
                    .Columns("NodeSiteID")
                    .FirstOrDefault();

                // Get Settings based on the Page itself
                Settings = GetNodeItemBuilderSettings(Page.NodeAliasPath, GetSite(Page.NodeSiteID).SiteName, true, false);
            }

            // Build and save
            NodeItem GivenNodeItem = new NodeItem(NodeID, Settings);
            if (ErrorOnConflict())
            {
                // If error on conflict, everything MUST be done syncly.
                GivenNodeItem.BuildChildren();
                if (GivenNodeItem.ConflictsExist())
                {
                    throw new Exception("Conflict Exists, aborting save");
                }
                // Save changes
                GivenNodeItem.SaveChanges();
            }
            else
            {
                // Do rest asyncly.
                QueueUpUrlSlugGeneration(GivenNodeItem);
            }
        }

        #region "Cached Helpers"

        // TO DO, get from setting
        public static bool ErrorOnConflict()
        {
            return false;
        }

        public static SiteInfo GetSite(int SiteID)
        {
            return CacheHelper.Cache(cs =>
            {
                var Site = SiteInfoProvider.GetSiteInfo(SiteID);
                if(cs.Cached) {
                }
                cs.CacheDependency = CacheHelper.GetCacheDependency("cms.site|byid|" + SiteID);
                return Site;
            }, new CacheSettings(1440, "GetSiteByID", SiteID));
        }

        public static DataClassInfo GetClass(string ClassName)
        {
            return CacheHelper.Cache(cs =>
            {
                var Class = DataClassInfoProvider.GetDataClassInfo(ClassName);
                cs.CacheDependency = CacheHelper.GetCacheDependency("cms.class|byname|" + ClassName);
                return Class;
            }, new CacheSettings(1440, "GetClassByName", ClassName));
        }

        /// <summary>
        /// Cached helper to get the CultureInfo object for the given Culture code
        /// </summary>
        /// <param name="CultureCode">the Culture code (ex en-US)</param>
        /// <returns>The Culture Info</returns>
        public static CultureInfo GetCulture(string CultureCode)
        {
            return CacheHelper.Cache<CultureInfo>(cs =>
            {
                var Culture = CultureInfoProvider.GetCultureInfo(CultureCode);
                cs.CacheDependency = CacheHelper.GetCacheDependency("cms.culture|byname|" + CultureCode);
                return Culture;
            }, new CacheSettings(1440, "GetCultureByName", CultureCode));
        }

        #endregion  

    }


}