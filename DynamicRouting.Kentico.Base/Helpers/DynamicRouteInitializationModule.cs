﻿using CMS;
using CMS.Base;
using CMS.DataEngine;
using CMS.DocumentEngine;
using CMS.EventLog;
using CMS.Helpers;
using CMS.MacroEngine;
using CMS.Membership;
using CMS.Modules;
using CMS.Scheduler;
using CMS.SiteProvider;
using CMS.Synchronization;
using CMS.WorkflowEngine;
using DynamicRouting.Kentico;
using DynamicRouting.Kentico.Classes;
using System;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Transactions;

namespace DynamicRouting.Kentico
{
    /// <summary>
    /// This is the base OnInit, since the this is run on both the Mother and MVC, but we cannot initialize both modules (or it throw a duplicate error), this is called from a MVC and Mother specific initialization module
    /// </summary>
    public class DynamicRouteInitializationModule_Base
    {
        public DynamicRouteInitializationModule_Base()
        {

        }

        public void Init()
        {
            // Ensure that the Foreign Keys and Views exist
            if (ResourceInfoProvider.GetResourceInfo("DynamicRouting.Kentico") != null)
            {
                try
                {
                    ConnectionHelper.ExecuteNonQuery("DynamicRouting.UrlSlug.InitializeSQLEntities");
                }
                catch (Exception ex)
                {
                    EventLogProvider.LogException("DynamicRouting", "ErrorRunningSQLEntities", ex, additionalMessage: "Could not run DynamicRouting.UrlSlug.InitializeSQLEntities Query, this sets up Views and Foreign Keys vital to operation.  Please ensure these queries exist.");
                }
            }
            // Create Scheduled Tasks if it doesn't exist
            if (TaskInfoProvider.GetTasks().WhereEquals("TaskName", "CheckUrlSlugQueue").Count == 0)
            {
                try
                {
                    TaskInfo CheckUrlSlugQueueTask = new TaskInfo()
                    {
                        TaskName = "CheckUrlSlugQueue",
                        TaskDisplayName = "Dynamic Routing - Check Url Slug Generation Queue",
                        TaskAssemblyName = "DynamicRouting.Kentico",
                        TaskClass = "DynamicRouting.Kentico.DynamicRouteScheduledTasks",
                        TaskInterval = "hour;11/3/2019 4:54:30 PM;1;00:00:00;23:59:00;Monday,Tuesday,Wednesday,Thursday,Friday,Saturday,Sunday",
                        TaskDeleteAfterLastRun = false,
                        TaskRunInSeparateThread = true,
                        TaskAllowExternalService = true,
                        TaskUseExternalService = false,
                        TaskRunIndividuallyForEachSite = false,
                        TaskEnabled = true,
                        TaskData = ""
                    };
                    CheckUrlSlugQueueTask.SetValue("TaskData", "");
                    TaskInfoProvider.SetTaskInfo(CheckUrlSlugQueueTask);
                }
                catch (Exception ex)
                {
                    EventLogProvider.LogException("DynamimcRouting", "ErrorCreatingUrlSlugQueue", ex, additionalMessage: "Could not create the CheckUrlSlugQueue scheduled task, please create a task with name 'CheckUrlSlugQueue' using assembly 'DynamicRouting.Kentico.DynamicRouteScheduledTasks' to run hourly.");
                }

            }

            // Detect Site Culture changes
            CultureSiteInfo.TYPEINFO.Events.Insert.After += CultureSite_InsertDelete_After;
            CultureSiteInfo.TYPEINFO.Events.Delete.After += CultureSite_InsertDelete_After;

            // Catch Site Default Culture and Builder setting updates
            SettingsKeyInfo.TYPEINFO.Events.Insert.After += SettingsKey_InsertUpdate_After;
            SettingsKeyInfo.TYPEINFO.Events.Update.After += SettingsKey_InsertUpdate_After;

            // Catch ClassURLPattern changes
            DataClassInfo.TYPEINFO.Events.Update.Before += DataClass_Update_Before;
            DataClassInfo.TYPEINFO.Events.Update.After += DataClass_Update_After;

            // Document Changes
            DocumentEvents.ChangeOrder.After += Document_ChangeOrder_After;
            DocumentEvents.Copy.After += Document_Copy_After;
            DocumentEvents.Delete.After += Document_Delete_After;
            DocumentEvents.Insert.After += Document_Insert_After;
            DocumentEvents.InsertLink.After += Document_InsertLink_After;
            DocumentEvents.InsertNewCulture.After += Document_InsertNewCulture_After;
            DocumentEvents.Move.Before += Document_Move_Before;
            DocumentEvents.Move.After += Document_Move_After;
            DocumentEvents.Sort.After += Document_Sort_After;
            DocumentEvents.Update.After += Document_Update_After;
            WorkflowEvents.Publish.After += Document_Publish_After;

            // Handle 301 Redirect creation on Url Slug updates
            UrlSlugInfo.TYPEINFO.Events.Update.Before += UrlSlug_Update_Before_301Redirect;

            // Handle if IsCustom was true and is now false to re-build the slug
            UrlSlugInfo.TYPEINFO.Events.Update.Before += UrlSlug_Update_Before_IsCustomRebuild;
            UrlSlugInfo.TYPEINFO.Events.Update.After += UrlSlug_Update_After_IsCustomRebuild;

            // Update Task Title to so we can handle differently depending on the type of update
            StagingEvents.LogTask.Before += StagingTask_LogTask_Before;
            StagingEvents.ProcessTask.Before += StagingTask_ProcessTask_Before;
        }

        private void StagingTask_ProcessTask_Before(object sender, StagingSynchronizationEventArgs e)
        {
            if (e.ObjectType.Equals(UrlSlugInfo.OBJECT_TYPE, StringComparison.InvariantCultureIgnoreCase))
            {
                // Get the URL slug itself
                UrlSlugInfo UrlSlug = new UrlSlugInfo(e.TaskData.Tables[0].Rows[0]);
                using (new CMSActionContext()
                {
                    LogSynchronization = false,
                    LogIntegration = false
                })
                {
                    if (e.TaskData.Tables.Contains("urlslugtype"))
                    {
                        DataTable SlugType = e.TaskData.Tables["urlslugtype"];
                        int NewNodeID = TranslateBindingTranslateID(UrlSlug.UrlSlugNodeID, e.TaskData, "cms.node");
                        // Get the Site's current Url Slug
                        UrlSlugInfo CurrentUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                            .WhereEquals("UrlSlugNodeID", NewNodeID)
                            .WhereEquals("UrlSlugCultureCode", UrlSlug.UrlSlugCultureCode)
                            .FirstOrDefault();
                        if (SlugType.Rows.Count > 0 && CurrentUrlSlug != null)
                        {
                            switch (ValidationHelper.GetString(SlugType.Rows[0]["typecode"], "check").ToLower())
                            {
                                case "addupdate":
                                    if (!CurrentUrlSlug.UrlSlugIsCustom)
                                    {
                                        CurrentUrlSlug.UrlSlugIsCustom = true;
                                        CurrentUrlSlug.UrlSlug = UrlSlug.UrlSlug;
                                        UrlSlugInfoProvider.SetUrlSlugInfo(CurrentUrlSlug);
                                    }
                                    else if (CurrentUrlSlug.UrlSlug != UrlSlug.UrlSlug)
                                    {
                                        CurrentUrlSlug.UrlSlug = UrlSlug.UrlSlug;
                                        UrlSlugInfoProvider.SetUrlSlugInfo(UrlSlug);
                                    }
                                    e.TaskHandled = true;
                                    break;
                                case "remove":
                                    if (CurrentUrlSlug.UrlSlugIsCustom)
                                    {
                                        CurrentUrlSlug.UrlSlugIsCustom = false;
                                        UrlSlugInfoProvider.SetUrlSlugInfo(CurrentUrlSlug);
                                    }
                                    e.TaskHandled = true;
                                    break;
                                case "check":
                                    bool UpdateCurrentUrlSlug = false;
                                    if (UrlSlug.UrlSlugIsCustom)
                                    {
                                        if (!CurrentUrlSlug.UrlSlugIsCustom)
                                        {
                                            CurrentUrlSlug.UrlSlugIsCustom = true;
                                            UpdateCurrentUrlSlug = true;
                                        }
                                        if (CurrentUrlSlug.UrlSlug != UrlSlug.UrlSlug)
                                        {
                                            CurrentUrlSlug.UrlSlug = UrlSlug.UrlSlug;
                                            UpdateCurrentUrlSlug = true;
                                        }
                                    }
                                    else
                                    {
                                        if (CurrentUrlSlug.UrlSlugIsCustom)
                                        {
                                            CurrentUrlSlug.UrlSlugIsCustom = false;
                                            UpdateCurrentUrlSlug = true;
                                        }
                                    }
                                    if (UpdateCurrentUrlSlug)
                                    {
                                        UrlSlugInfoProvider.SetUrlSlugInfo(CurrentUrlSlug);
                                    }
                                    e.TaskHandled = true;
                                    break;
                            }
                        }
                    }
                }
            }
        }

        public static int TranslateBindingTranslateID(int ItemID, DataSet TaskData, string classname)
        {
            DataTable ObjectTranslationTable = TaskData.Tables.Cast<DataTable>().Where(x => x.TableName.ToLower() == "objecttranslation").FirstOrDefault();
            if (ObjectTranslationTable == null)
            {
                EventLogProvider.LogEvent("E", "RelHelper.TranslateBindingTranslateID", "NoObjectTranslationTable", "Could not find an ObjectTranslation table in the Task Data, please make sure to only call this with a task that has an ObjectTranslation table");
                return -1;
            }
            foreach (DataRow ItemDR in ObjectTranslationTable.Rows.Cast<DataRow>()
                .Where(x => ValidationHelper.GetString(x["ObjectType"], "").ToLower() == classname.ToLower() && ValidationHelper.GetInteger(x["ID"], -1) == ItemID))
            {
                int TranslationID = ValidationHelper.GetInteger(ItemDR["ID"], 0);
                if (ItemID == TranslationID)
                {
                    GetIDParameters ItemParams = new GetIDParameters();
                    if (ValidationHelper.GetGuid(ItemDR["GUID"], Guid.Empty) != Guid.Empty)
                    {
                        ItemParams.Guid = (Guid)ItemDR["GUID"];
                    }
                    if (!string.IsNullOrWhiteSpace(ValidationHelper.GetString(ItemDR["CodeName"], "")))
                    {
                        ItemParams.CodeName = (string)ItemDR["CodeName"];
                    }
                    if (ObjectTranslationTable.Columns.Contains("SiteName") && !string.IsNullOrWhiteSpace(ValidationHelper.GetString(ItemDR["SiteName"], "")))
                    {
                        int SiteID = SiteInfoProvider.GetSiteID((string)ItemDR["SiteName"]);
                        if (SiteID > 0)
                        {
                            ItemParams.SiteId = SiteID;
                        }
                    }
                    try
                    {
                        int NewID = TranslationHelper.GetIDFromDB(ItemParams, classname);
                        if (NewID > 0)
                        {
                            ItemID = NewID;
                        }
                    }
                    catch (Exception ex)
                    {
                        EventLogProvider.LogException("RelHelper.TranslateBindingTranslateID", "No Translation Found", ex, additionalMessage: "No Translation found.");
                        return -1;
                    }
                }
            }
            return ItemID;
        }

        private void StagingTask_LogTask_Before(object sender, StagingLogTaskEventArgs e)
        {
            if (e.Task.TaskObjectType.Equals(UrlSlugInfo.OBJECT_TYPE, StringComparison.InvariantCultureIgnoreCase))
            {
                UrlSlugInfo UrlSlug = (UrlSlugInfo)e.Object;
                RecursionControl AddedTrigger = new RecursionControl($"LogStagingTask_AddedUpdatedCustom_" + UrlSlug.UrlSlugGuid);
                RecursionControl RemovedTrigger = new RecursionControl($"LogStagingTask_RemovedCustom_" + UrlSlug.UrlSlugGuid);
                RecursionControl IndividualUpdateTrigger = new RecursionControl("LogStagingTask_CameFromIndividualUpdate_" + UrlSlug.UrlSlugGuid);

                // Alters the Task Data, adding a new 'table' to the task data, this will be used by the processing to know what to do with it.
                if (!IndividualUpdateTrigger.Continue)
                {
                    if (!AddedTrigger.Continue)
                    {
                        e.Task.TaskTitle.Replace("Update Url Slug", "Add or Update Custom Url Slug");
                        e.Task.TaskData = e.Task.TaskData.Replace("</NewDataSet>", "<urlslugtype><typecode>addupdate</typecode></urlslugtype></NewDataSet>");
                    }
                    else if (!RemovedTrigger.Continue)
                    {
                        e.Task.TaskTitle.Replace("Update Url Slug", "Remove Custom Url Slug");
                        e.Task.TaskData = e.Task.TaskData.Replace("</NewDataSet>", "<urlslugtype><typecode>remove</typecode></urlslugtype></NewDataSet>");
                    }
                }
                else
                {
                    e.Task.TaskData = e.Task.TaskData.Replace("</NewDataSet>", "<urlslugtype><typecode>check</typecode></urlslugtype></NewDataSet>");
                }
            }
        }



        private void UrlSlug_Update_After_IsCustomRebuild(object sender, ObjectEventArgs e)
        {
            UrlSlugInfo UrlSlug = (UrlSlugInfo)e.Object;
            RecursionControl Trigger = new RecursionControl("UrlSlugNoLongerCustom_" + UrlSlug.UrlSlugGuid);
            if (!Trigger.Continue)
            {
                try
                {
                    using (new CMSActionContext()
                    {
                        LogSynchronization = false,
                        LogIntegration = false
                    })
                    {
                        // If Continue is false, then the Before update shows this needs to be rebuilt.
                        DynamicRouteInternalHelper.RebuildRoutesByNode(UrlSlug.UrlSlugNodeID);
                    }
                }
                catch (UrlSlugCollisionException ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Url Slug {UrlSlug.UrlSlugID}");
                    e.Cancel();
                }
                catch (Exception ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Url Slug {UrlSlug.UrlSlugID}");
                }
            }
        }

        private void UrlSlug_Update_Before_IsCustomRebuild(object sender, ObjectEventArgs e)
        {
            UrlSlugInfo UrlSlug = (UrlSlugInfo)e.Object;
            TreeNode Node = new DocumentQuery()
                    .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                    .Columns("NodeSiteID, NodeAliasPath, NodeGuid, NodeAlias")
                    .FirstOrDefault();

            // First check if there is already a custom URL slug, if so cancel
            if (UrlSlug.UrlSlugIsCustom)
            {

                var ExistingMatchingSlug = UrlSlugInfoProvider.GetUrlSlugs()
                    .WhereNotEquals("UrlSlugNodeID", UrlSlug.UrlSlugNodeID)
                    .WhereEquals("UrlSlug", UrlSlug.UrlSlug)
                    .Where($"UrlSlugNodeID in (Select NodeID from CMS_Tree where NodeSiteID = {Node.NodeSiteID})")
                    .Columns("UrlSlugNodeID")
                    .FirstOrDefault();
                if (ExistingMatchingSlug != null)
                {
                    TreeNode ConflictNode = new DocumentQuery()
                    .WhereEquals("NodeID", ExistingMatchingSlug.UrlSlugNodeID)
                    .Columns("NodeSiteID, NodeAliasPath")
                    .FirstOrDefault();
                    var Error = new NotSupportedException($"This custom URL Slug '{UrlSlug.UrlSlug}' on {Node.NodeAliasPath} is already the pattern of an existing document ({ConflictNode.NodeAliasPath}).  Operation aborted.");
                    EventLogProvider.LogException("DynamicRouting", "CustomUrlSlugConflict", Error, Node.NodeSiteID);
                    throw Error;
                }
            }

            // If it was not custom and now is, create a "Customized" staging task.
            RecursionControl IndividualUpdateTrigger = new RecursionControl("UrlSlug_CameFromIndividualUpdate_" + UrlSlug.UrlSlugGuid);
            // Both trigger, and also if this is the first run through run this check
            if (IndividualUpdateTrigger.Continue)
            {
                if (UrlSlug.UrlSlugIsCustom && !ValidationHelper.GetBoolean(UrlSlug.GetOriginalValue("UrlSlugIsCustom"), true)
                    ||
                    UrlSlug.UrlSlug != ValidationHelper.GetString(UrlSlug.GetOriginalValue("UrlSlug"), UrlSlug.UrlSlug)
                    )
                {
                    RecursionControl AddedUpdatedTrigger = new RecursionControl("UrlSlug_AddedUpdatedCustom_" + UrlSlug.UrlSlugGuid);
                    bool AddedUpdatedTriggered = AddedUpdatedTrigger.Continue;

                    /*if (StagingTaskInfoProvider.LogContentChanges(SiteInfoProvider.GetSiteName(Node.NodeSiteID)))
                    {
                        StagingTaskInfo CustomizedUrlSlugTask = new StagingTaskInfo()
                        {
                            TaskTitle = $"Custom Url Slug for '{Node.NodeAlias}' created/updated",
                            TaskSiteID = Node.NodeSiteID,
                            TaskType = TaskTypeEnum.UpdateObject,
                            TaskObjectType = UrlSlugInfo.OBJECT_TYPE,
                            TaskObjectID = DataClassInfoProvider.GetDataClassInfo(UrlSlugInfo.OBJECT_TYPE).ClassID,
                            TaskServers = string.Join(";", ServerInfoProvider.GetServers().WhereEquals("ServerEnabled", true).Columns("ServerName").Select(x => x.ServerName)),
                            TaskTime = DateTime.Now
                        };
                        CustomizedUrlSlugTask.TaskData = GetUrlSlugTaskData(UrlSlug, Node);
                        StagingTaskInfoProvider.SetTaskInfo(CustomizedUrlSlugTask);
                        StagingTaskUserInfoProvider.AddStagingTaskToUser(CustomizedUrlSlugTask.TaskID, MembershipContext.AuthenticatedUser.UserID);
                        SynchronizationInfoProvider.CreateSynchronizationRecords(CustomizedUrlSlugTask.TaskID, ServerInfoProvider.GetServers().WhereEquals("ServerEnabled", true).Columns("ServerID").Select(x => x.ServerID));
                    }*/
                }
                if (!UrlSlug.UrlSlugIsCustom && ValidationHelper.GetBoolean(UrlSlug.GetOriginalValue("UrlSlugIsCustom"), false))
                {
                    RecursionControl RemovedTrigger = new RecursionControl("UrlSlug_RemovedCustom_" + UrlSlug.UrlSlugGuid);
                    bool RemovedTriggered = RemovedTrigger.Continue;
                    /*
                    if (StagingTaskInfoProvider.LogContentChanges(SiteInfoProvider.GetSiteName(Node.NodeSiteID)))
                    {
                        StagingTaskInfo CustomizedUrlSlugTask = new StagingTaskInfo()
                        {
                            TaskTitle = $"Custom Url Slug for '{Node.NodeAlias}' removed",
                            TaskSiteID = Node.NodeSiteID,
                            TaskType = TaskTypeEnum.UpdateObject,
                            TaskObjectType = UrlSlugInfo.OBJECT_TYPE,
                            TaskObjectID = DataClassInfoProvider.GetDataClassInfo(UrlSlugInfo.OBJECT_TYPE).ClassID,
                            TaskServers = string.Join(";", ServerInfoProvider.GetServers().WhereEquals("ServerEnabled", true).Columns("ServerName").Select(x => x.ServerName)),
                            TaskTime = DateTime.Now
                        };
                        CustomizedUrlSlugTask.TaskData = GetUrlSlugTaskData(UrlSlug, Node);
                        StagingTaskInfoProvider.SetTaskInfo(CustomizedUrlSlugTask);
                        StagingTaskUserInfoProvider.AddStagingTaskToUser(CustomizedUrlSlugTask.TaskID, MembershipContext.AuthenticatedUser.UserID);
                        SynchronizationInfoProvider.CreateSynchronizationRecords(CustomizedUrlSlugTask.TaskID, ServerInfoProvider.GetServers().WhereEquals("ServerEnabled", true).Columns("ServerID").Select(x => x.ServerID));
                    }*/
                }
            }

            // If the Url Slug is custom or was custom, then need to rebuild after.
            if (UrlSlug.UrlSlugIsCustom || ValidationHelper.GetBoolean(UrlSlug.GetOriginalValue("UrlSlugIsCustom"), UrlSlug.UrlSlugIsCustom))
            {
                // Add hook so the Url Slug will be re-rendered after it's updated
                RecursionControl Trigger = new RecursionControl("UrlSlugNoLongerCustom_" + UrlSlug.UrlSlugGuid);
                var Triggered = Trigger.Continue;
            }
        }

        private string GetUrlSlugTaskData(UrlSlugInfo urlSlug, TreeNode node)
        {
            TranslationHelper NodeBoundObjectTableHelper = new TranslationHelper();

            //DataSet UrlSlugDS = new DataSet("NewDataSet");
            //DataTable UrlSlugTable = new DataTable(UrlSlugInfo.OBJECT_TYPE.ToLower().Replace(".", "_"));

            // Create Base Dataset of UrlSlug
            DataSet UrlSlugDS = SynchronizationHelper.GetObjectData(OperationTypeEnum.Synchronization, urlSlug, false, false, NodeBoundObjectTableHelper);

            // Create Translation Table of Node
            DataSet NodeBoundObjectData = SynchronizationHelper.GetObjectsData(OperationTypeEnum.Synchronization, node, string.Format($"NodeID = {node.NodeID}"), null, true, false, NodeBoundObjectTableHelper);
            if (NodeBoundObjectTableHelper.TranslationTable != null && NodeBoundObjectTableHelper.TranslationTable.Rows.Count > 0)
            {
                NodeBoundObjectData.Tables.Add(NodeBoundObjectTableHelper.TranslationTable);
            }

            // Convert to XML and Back, this makes the Columns all type string so the transfer table works
            DataSet NodeRegionObjectDataHolder = new DataSet();
            NodeRegionObjectDataHolder.ReadXml(new StringReader(NodeBoundObjectData.GetXml()));
            if (!DataHelper.DataSourceIsEmpty(NodeRegionObjectDataHolder) && NodeRegionObjectDataHolder.Tables.Count > 0)
            {
                DataHelper.TransferTables(UrlSlugDS, NodeRegionObjectDataHolder);
            }

            return UrlSlugDS.GetXml();
        }

        private void Document_Publish_After(object sender, WorkflowEventArgs e)
        {
            // Update the document itself
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Document.NodeID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Publish After of Node {e.Document.NodeID} [{e.Document.NodeAliasPath}]");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Publish After of Node {e.Document.NodeID} [{e.Document.NodeAliasPath}]");
            }
        }


        private void UrlSlug_Update_Before_301Redirect(object sender, ObjectEventArgs e)
        {
            UrlSlugInfo UrlSlug = (UrlSlugInfo)e.Object;

            #region "Create Alternative Url of Previous Url"
            try
            {
                // Alternative Urls don't have the slash at the beginning
                string OriginalUrlSlugNoTrim = ValidationHelper.GetString(UrlSlug.GetOriginalValue("UrlSlug"), UrlSlug.UrlSlug);
                string OriginalUrlSlug = OriginalUrlSlugNoTrim.Trim('/');

                // save previous Url to 301 redirects
                // Get DocumentID
                var Document = DocumentHelper.GetDocuments()
                    .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                    .CombineWithDefaultCulture()
                    .CombineWithAnyCulture()
                    .Culture(UrlSlug.UrlSlugCultureCode)
                    .FirstOrDefault();
                var AlternativeUrl = AlternativeUrlInfoProvider.GetAlternativeUrls()
                    .WhereEquals("AlternativeUrlUrl", OriginalUrlSlug)
                    .FirstOrDefault();

                SiteInfo Site = SiteInfoProvider.GetSiteInfo(Document.NodeSiteID);
                string DefaultCulture = SettingsKeyInfoProvider.GetValue("CMSDefaultCultureCode", new SiteInfoIdentifier(Site.SiteName));

                UrlSlugInfo CultureSiblingUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                    .WhereEquals("UrlSlug", OriginalUrlSlugNoTrim)
                    .WhereEquals("UrlSlugNodeID", UrlSlug.UrlSlugNodeID)
                    .WhereNotEquals("UrlSlugCultureCode", UrlSlug.UrlSlugCultureCode)
                    .FirstOrDefault();

                if (AlternativeUrl != null)
                {
                    if (AlternativeUrl.AlternativeUrlDocumentID != Document.DocumentID)
                    {
                        // If Same NodeID, then make sure the DocumentID is of the one that is the DefaultCulture, if no DefaultCulture
                        // Exists, then just ignore
                        var AlternativeUrlDocument = DocumentHelper.GetDocument(AlternativeUrl.AlternativeUrlDocumentID, new TreeProvider());

                        // Log a warning
                        if (AlternativeUrlDocument.NodeID != UrlSlug.UrlSlugNodeID)
                        {
                            EventLogProvider.LogEvent("W", "DynamicRouting", "AlternativeUrlConflict", eventDescription: string.Format("Conflict between Alternative Url '{0}' exists for Document {1} [{2}] which already exists as an Alternative Url for Document {3} [{4}].",
                                AlternativeUrl.AlternativeUrlUrl,
                                Document.NodeAliasPath,
                                Document.DocumentCulture,
                                AlternativeUrlDocument.NodeAliasPath,
                                AlternativeUrlDocument.DocumentCulture
                                ));
                        }
                        TreeNode DefaultLanguage = DocumentHelper.GetDocuments()
                            .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                            .Culture(DefaultCulture)
                            .CombineWithDefaultCulture()
                            .FirstOrDefault();

                        // Save only if there is no default language, or it is the default language, or if there is a default language adn it isn't it, that the Url doesn't match
                        // Any of the default languages urls, as this often happens when you clone from an existing language and then save a new url.
                        bool DefaultLanguageExists = DefaultLanguage != null;
                        bool IsNotDefaultLanguage = DefaultLanguageExists && AlternativeUrl.AlternativeUrlDocumentID != DefaultLanguage.DocumentID;
                        bool MatchesDefaultLang = false;
                        if (DefaultLanguageExists && IsNotDefaultLanguage)
                        {
                            // See if the OriginalUrlSlug matches the default document, or one of it's alternates
                            var DefaultLangUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                                .WhereEquals("UrlSlugNodeID", UrlSlug.UrlSlugNodeID)
                                .WhereEquals("UrlSlugCultureCode", DefaultLanguage.DocumentCulture)
                                .WhereEquals("UrlSlug", "/" + OriginalUrlSlug)
                                .FirstOrDefault();
                            var DefaultLangAltUrl = AlternativeUrlInfoProvider.GetAlternativeUrls()
                                .WhereEquals("AlternativeUrlDocumentID", DefaultLanguage.DocumentID)
                                .WhereEquals("AlternativeUrlUrl", OriginalUrlSlug)
                                .FirstOrDefault();
                            MatchesDefaultLang = DefaultLangUrlSlug != null || DefaultLangAltUrl != null;
                        }

                        if (!DefaultLanguageExists || !IsNotDefaultLanguage || (DefaultLanguageExists && IsNotDefaultLanguage && !MatchesDefaultLang))
                        {
                            AlternativeUrl.AlternativeUrlDocumentID = DefaultLanguage.DocumentID;
                            AlternativeUrlInfoProvider.SetAlternativeUrlInfo(AlternativeUrl);
                        }
                    }
                }
                // Create new one if there are no other Url Slugs with the same pattern for that node
                else if (CultureSiblingUrlSlug == null)
                {
                    AlternativeUrl = new AlternativeUrlInfo()
                    {
                        AlternativeUrlDocumentID = Document.DocumentID,
                        AlternativeUrlSiteID = Document.NodeSiteID,
                    };
                    AlternativeUrl.SetValue("AlternativeUrlUrl", OriginalUrlSlug);

                    // Save only if there is no default language, or it is the default language, or if there is a default language adn it isn't it, that the Url doesn't match
                    // Any of the default languages urls, as this often happens when you clone from an existing language and then save a new url.
                    TreeNode DefaultLanguage = DocumentHelper.GetDocuments()
                            .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                            .Culture(DefaultCulture)
                            .FirstOrDefault();
                    bool DefaultLanguageExists = DefaultLanguage != null;
                    bool IsNotDefaultLanguage = DefaultLanguageExists && AlternativeUrl.AlternativeUrlDocumentID != DefaultLanguage.DocumentID;
                    bool MatchesDefaultLang = false;
                    if (DefaultLanguageExists && IsNotDefaultLanguage)
                    {
                        // See if the OriginalUrlSlug matches the default document, or one of it's alternates
                        var DefaultLangUrlSlug = UrlSlugInfoProvider.GetUrlSlugs()
                            .WhereEquals("UrlSlugNodeID", UrlSlug.UrlSlugNodeID)
                            .WhereEquals("UrlSlugCultureCode", DefaultLanguage.DocumentCulture)
                            .WhereEquals("UrlSlug", "/" + OriginalUrlSlug)
                            .FirstOrDefault();
                        var DefaultLangAltUrl = AlternativeUrlInfoProvider.GetAlternativeUrls()
                            .WhereEquals("AlternativeUrlDocumentID", DefaultLanguage.DocumentID)
                            .WhereEquals("AlternativeUrlUrl", OriginalUrlSlug)
                            .FirstOrDefault();
                        MatchesDefaultLang = DefaultLangUrlSlug != null || DefaultLangAltUrl != null;
                    }
                    if (!DefaultLanguageExists || !IsNotDefaultLanguage || (DefaultLanguageExists && IsNotDefaultLanguage && !MatchesDefaultLang))
                    {
                        try
                        {
                            AlternativeUrlInfoProvider.SetAlternativeUrlInfo(AlternativeUrl);
                        }
                        catch (InvalidAlternativeUrlException ex)
                        {
                            // Figure out what to do, it doesn't match the pattern constraints.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "AlternateUrlError", $"Occurred on Url Slug Update for Url Slug {UrlSlug.UrlSlug} {UrlSlug.UrlSlugCultureCode}");
            }
            #endregion

            #region "Remove any Alternative Url of the new Url for this node"
            try
            {
                // Alternative Urls don't have the slash at the beginning
                string NewUrlSlug = UrlSlug.UrlSlug.Trim('/');

                // Check for any Alternative Urls for this node that match and remove
                // Get DocumentID
                var Document = DocumentHelper.GetDocuments()
                    .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                    .CombineWithDefaultCulture()
                    .CombineWithAnyCulture()
                    .Culture(UrlSlug.UrlSlugCultureCode)
                    .FirstOrDefault();
                var AllDocumentIDs = DocumentHelper.GetDocuments()
                    .WhereEquals("NodeID", UrlSlug.UrlSlugNodeID)
                    .AllCultures()
                    .Columns("DocumentID")
                    .Select(x => x.DocumentID).ToList();

                // Delete any Alternate Urls for any of the culture variations of this node that match the new Url Slug, this is to prevent infinite redirect loops
                AlternativeUrlInfoProvider.GetAlternativeUrls()
                    .WhereEquals("AlternativeUrlUrl", NewUrlSlug)
                    .WhereIn("AlternativeUrlDocumentID", AllDocumentIDs)
                    .ForEachObject(x => x.Delete());

                SiteInfo Site = SiteInfoProvider.GetSiteInfo(Document.NodeSiteID);
                string DefaultCulture = SettingsKeyInfoProvider.GetValue("CMSDefaultCultureCode", new SiteInfoIdentifier(Site.SiteName));

                var AltUrlsOnOtherNodes = AlternativeUrlInfoProvider.GetAlternativeUrls()
                    .WhereEquals("AlternativeUrlUrl", NewUrlSlug)
                    .WhereNotIn("AlternativeUrlDocumentID", AllDocumentIDs)
                    .ToList();

                // Add warning about conflict.
                if (AltUrlsOnOtherNodes.Count > 0)
                {
                    EventLogProvider.LogEvent("W", "DynamicRouting", "AlternateUrlConflict", $"Another page with an alternate Url matching {UrlSlug.UrlSlug} was found, please adjust and correct.");
                }

            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "AlternateUrlError", $"Occurred on Url Slug Update for Url Slug {UrlSlug.UrlSlug} {UrlSlug.UrlSlugCultureCode}");
            }

            #endregion
        }

        private void Document_Update_After(object sender, DocumentEventArgs e)
        {
            // Update the document itself, only if there is no workflow
            try
            {
                if (e.Node.WorkflowStep == null)
                {
                    DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
                }
                else
                {
                    if (e.Node.WorkflowStep.StepIsPublished && DynamicRouteInternalHelper.ErrorOnConflict())
                    {
                        DynamicRouteEventHelper.DocumentInsertUpdated_CheckOnly(e.Node.NodeID);
                    }
                }
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Update After for ${e.Node.NodeAlias}.");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Update After for ${e.Node.NodeAlias}.");
                e.Cancel();
            }
        }

        private static void LogErrorsInSeparateThread(Exception ex, string Source, string EventCode, string Description)
        {
            CMSThread LogErrorsThread = new CMSThread(new ThreadStart(() => LogErrors(ex, Source, EventCode, Description)), new ThreadSettings()
            {
                Mode = ThreadModeEnum.Async,
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                UseEmptyContext = false,
                CreateLog = true
            });
            LogErrorsThread.Start();
        }

        /// <summary>
        /// Async helper method, grabs the Queue that is set to be "running" for this application and processes.
        /// </summary>
        private static void LogErrors(Exception ex, string Source, string EventCode, string Description)
        {
            EventLogProvider.LogException(Source, EventCode, ex, additionalMessage: Description);
        }


        private void Document_Sort_After(object sender, DocumentSortEventArgs e)
        {
            // Check parent which will see if Children need update
            try
            {
                DynamicRouteInternalHelper.RebuildRoutesByNode(e.ParentNodeId);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Sort Update After for Parent Node ${e.ParentNodeId}.");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Sort Update After for Parent Node ${e.ParentNodeId}.");
            }
        }

        private void Document_Move_Before(object sender, DocumentEventArgs e)
        {
            // Add track of the Document's original Parent ID so we can rebuild on that after moved.
            try
            {
                var Slot = Thread.GetNamedDataSlot("PreviousParentIDForNode_" + e.Node.NodeID);
                if (Slot == null)
                {
                    Slot = Thread.AllocateNamedDataSlot("PreviousParentIDForNode_" + e.Node.NodeID);
                }
                Thread.SetData(Slot, e.Node.NodeParentID);
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Move Before for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_Move_After(object sender, DocumentEventArgs e)
        {
            // Update on the Node itself, this will rebuild itself and it's children
            DynamicRouteInternalHelper.CommitTransaction(true);
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);

                var PreviousParentNodeID = Thread.GetData(Thread.GetNamedDataSlot("PreviousParentIDForNode_" + e.Node.NodeID));
                if (PreviousParentNodeID != null && (int)PreviousParentNodeID != e.TargetParentNodeID)
                {
                    // If differnet node IDs, it moved to another parent, so also run Document Moved check on both new and old parent
                    DynamicRouteEventHelper.DocumentMoved((int)PreviousParentNodeID, e.TargetParentNodeID);
                }
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document After Before for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Move After for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_InsertNewCulture_After(object sender, DocumentEventArgs e)
        {
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document New Culture After for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document New Culture After for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_InsertLink_After(object sender, DocumentEventArgs e)
        {
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Insert Link After for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Insert Link After for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_Insert_After(object sender, DocumentEventArgs e)
        {
            // Prevents the CHangeOrderAfter which may trigger before this from creating a double queue item.
            RecursionControl PreventInsertAfter = new RecursionControl("PreventInsertAfter" + e.Node.NodeID);
            if (PreventInsertAfter.Continue)
            {
                try
                {
                    DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
                }
                catch (UrlSlugCollisionException ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Insert After for Node {e.Node.NodeAliasPath}");
                    e.Cancel();
                }
                catch (Exception ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Insert After for Node {e.Node.NodeAliasPath}");
                }
            }
        }

        private void Document_Delete_After(object sender, DocumentEventArgs e)
        {
            try
            {
                DynamicRouteEventHelper.DocumentDeleted(e.Node.NodeParentID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Delete for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Delete for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_Copy_After(object sender, DocumentEventArgs e)
        {
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Copy for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Copy for Node {e.Node.NodeAliasPath}");
            }
        }

        private void Document_ChangeOrder_After(object sender, DocumentChangeOrderEventArgs e)
        {
            // Sometimes ChangeOrder is triggered before the insert (if it inserts before other records),
            // So will use recursion helper to prevent this from running on the insert as well.
            RecursionControl PreventInsertAfter = new RecursionControl("PreventInsertAfter" + e.Node.NodeID);
            var Trigger = PreventInsertAfter.Continue;
            try
            {
                DynamicRouteEventHelper.DocumentInsertUpdated(e.Node.NodeID);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Document Change Order for Node {e.Node.NodeAliasPath}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Document Change Order for Node {e.Node.NodeAliasPath}");
            }
        }

        private void DataClass_Update_Before(object sender, ObjectEventArgs e)
        {
            // Check if the Url Pattern is changing
            DataClassInfo Class = (DataClassInfo)e.Object;
            if (!Class.ClassURLPattern.Equals(ValidationHelper.GetString(e.Object.GetOriginalValue("ClassURLPattern"), "")))
            {
                // Add key that the "After" will check, if the Continue is "False" then this was hit, so we actually want to continue.
                RecursionControl TriggerClassUpdateAfter = new RecursionControl("TriggerClassUpdateAfter_" + Class.ClassName);
                var Trigger = TriggerClassUpdateAfter.Continue;
            }
        }

        private void DataClass_Update_After(object sender, ObjectEventArgs e)
        {
            DataClassInfo Class = (DataClassInfo)e.Object;
            RecursionControl PreventDoubleClassUpdateTrigger = new RecursionControl("PreventDoubleClassUpdateTrigger_" + Class.ClassName);

            // If the "Continue" is false, it means that a DataClass_Update_Before found that the UrlPattern was changed
            // Otherwise the "Continue" will be true that this is the first time triggering it.
            if (!new RecursionControl("TriggerClassUpdateAfter_" + Class.ClassName).Continue && PreventDoubleClassUpdateTrigger.Continue)
            {
                try
                {
                    DynamicRouteEventHelper.ClassUrlPatternChanged(Class.ClassName);
                }
                catch (UrlSlugCollisionException ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Class Update After for class {Class.ClassName}");
                    e.Cancel();
                }
                catch (Exception ex)
                {
                    LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Class Update After for class {Class.ClassName}");
                }
            }
        }

        private void SettingsKey_InsertUpdate_After(object sender, ObjectEventArgs e)
        {
            SettingsKeyInfo Key = (SettingsKeyInfo)e.Object;
            switch (Key.KeyName.ToLower())
            {
                case "cmsdefaultculturecode":
                    try
                    {
                        if (Key.SiteID > 0)
                        {
                            string SiteName = DynamicRouteInternalHelper.GetSite(Key.SiteID).SiteName;
                            DynamicRouteEventHelper.SiteDefaultLanguageChanged(SiteName);
                        }
                        else
                        {
                            foreach (string SiteName in SiteInfoProvider.GetSites().Select(x => x.SiteName))
                            {
                                DynamicRouteEventHelper.SiteDefaultLanguageChanged(SiteName);
                            }
                        }
                    }
                    catch (UrlSlugCollisionException ex)
                    {
                        LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Settings Key Update After for Key {Key.KeyName}");
                        e.Cancel();
                    }
                    catch (Exception ex)
                    {
                        LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Settings Key Update After for Key {Key.KeyName}");
                    }
                    break;
                case "generateculturevariationurlslugs":
                    try
                    {
                        if (Key.SiteID > 0)
                        {
                            string SiteName = DynamicRouteInternalHelper.GetSite(Key.SiteID).SiteName;
                            DynamicRouteEventHelper.CultureVariationSettingsChanged(SiteName);
                        }
                        else
                        {
                            foreach (string SiteName in SiteInfoProvider.GetSites().Select(x => x.SiteName))
                            {
                                DynamicRouteEventHelper.CultureVariationSettingsChanged(SiteName);
                            }
                        }
                    }
                    catch (UrlSlugCollisionException ex)
                    {
                        LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Settings Key Update After for Key {Key.KeyName}");
                        e.Cancel();
                    }
                    catch (Exception ex)
                    {
                        LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Settings Key Update After for Key {Key.KeyName}");
                    }
                    break;
            }
        }

        private void CultureSite_InsertDelete_After(object sender, ObjectEventArgs e)
        {
            CultureSiteInfo CultureSite = (CultureSiteInfo)e.Object;
            string SiteName = DynamicRouteInternalHelper.GetSite(CultureSite.SiteID).SiteName;
            try
            {
                DynamicRouteEventHelper.SiteLanguageChanged(SiteName);
            }
            catch (UrlSlugCollisionException ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "UrlSlugConflict", $"Occurred on Culture Site Insert/Delete for Site {SiteName}");
                e.Cancel();
            }
            catch (Exception ex)
            {
                LogErrorsInSeparateThread(ex, "DynamicRouting", "Error", $"Occurred on Culture Site Insert/Delete for Site {SiteName}");
            }
        }
    }
}
