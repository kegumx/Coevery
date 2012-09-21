﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Transactions;
using System.Web.Mvc;
using Orchard;
using Orchard.Autoroute.Models;
using Orchard.Autoroute.Services;
using Orchard.ContentManagement;
using Orchard.ContentManagement.MetaData;
using Orchard.Core.Common.Models;
using Orchard.Core.Containers.Models;
using Orchard.Core.Title.Models;
using Orchard.Data;
using Orchard.Environment.Configuration;
using Orchard.Localization;
using Orchard.Reports.Services;
using Orchard.Security;
using Orchard.UI.Admin;
using Orchard.UI.Notify;
using UpgradeTo15.ViewModels;

namespace UpgradeTo15.Controllers {
    [Admin]
    public class RouteController : Controller {
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IOrchardServices _orchardServices;
        private readonly ISessionFactoryHolder _sessionFactoryHolder;
        private readonly ShellSettings _shellSettings;
        private readonly IAutorouteService _autorouteService;
        private readonly IReportsCoordinator _reportsCoordinator;

        public RouteController(
            IContentDefinitionManager contentDefinitionManager,
            IOrchardServices orchardServices,
            ISessionFactoryHolder sessionFactoryHolder,
            ShellSettings shellSettings,
            IAutorouteService autorouteService,
            IReportsCoordinator reportsCoordinator) {
            _contentDefinitionManager = contentDefinitionManager;
            _orchardServices = orchardServices;
            _sessionFactoryHolder = sessionFactoryHolder;
            _shellSettings = shellSettings;
            _autorouteService = autorouteService;
            _reportsCoordinator = reportsCoordinator;
        }

        public Localizer T { get; set; }

        public ActionResult Index() {
            var viewModel = new MigrateViewModel { ContentTypes = new List<ContentTypeEntry>() };
            foreach (var contentType in _contentDefinitionManager.ListTypeDefinitions().OrderBy(c => c.Name)) {
                // only display routeparts
                if (contentType.Parts.Any(x => x.PartDefinition.Name == "RoutePart")) {
                    viewModel.ContentTypes.Add(new ContentTypeEntry {ContentTypeName = contentType.Name});
                }
            }

            if(!viewModel.ContentTypes.Any()) {
                _orchardServices.Notifier.Warning(T("There are no content types with RoutePart"));
            }

            return View(viewModel);
        }

        [HttpPost, ActionName("Index")]
        public ActionResult IndexPOST() {
            if (!_orchardServices.Authorizer.Authorize(StandardPermissions.SiteOwner, T("Not allowed to migrate routes.")))
                return new HttpUnauthorizedResult();

            var viewModel = new MigrateViewModel { ContentTypes = new List<ContentTypeEntry>() };

            if(TryUpdateModel(viewModel)) {

                // creating report
                _reportsCoordinator.Register("Migration", "UpgradeTo15", "Migrating " + string.Join(" ,", viewModel.ContentTypes.Where(x => x.IsChecked).Select(x => x.ContentTypeName).ToArray()));
            
                var contentTypesToMigrate = viewModel.ContentTypes.Where(c => c.IsChecked).Select(c => c.ContentTypeName);

                var sessionFactory = _sessionFactoryHolder.GetSessionFactory();
                var session = sessionFactory.OpenSession();

                foreach (var contentType in contentTypesToMigrate) {

                    _reportsCoordinator.Information("Migration", "Adding parts to " + contentType);

                    // migrating parts
                    _contentDefinitionManager.AlterTypeDefinition(contentType,
                        builder => builder
                                        .WithPart("AutoroutePart")
                                        .WithPart("TitlePart"));

                    // force the first object to be reloaded in order to get a valid AutoroutePart
                    _orchardServices.ContentManager.Flush();
                    _orchardServices.ContentManager.Clear();

                    var count = 0;
                    var isContainable = false;
                    IEnumerable<ContentItem> contents;
                    bool errors = false;

                    do {
                        contents = _orchardServices.ContentManager.HqlQuery().ForType(contentType).ForVersion(VersionOptions.Latest).Slice(count, 100).ToList();

                        foreach (dynamic content in contents) {
                            var autoroutePart = ((ContentItem)content).As<AutoroutePart>();
                            var titlePart = ((ContentItem) content).As<TitlePart>();
                            var commonPart = ((ContentItem) content).As<CommonPart>();
                            
                            if(commonPart != null && commonPart.Container != null) {
                                isContainable = true;
                            }

                            using (new TransactionScope(TransactionScopeOption.RequiresNew)) {
                                var command = session.Connection.CreateCommand();
                                command.CommandText = string.Format(@"
                                    SELECT Title, Path FROM {0} 
                                    INNER JOIN {1} ON {0}.Id = {1}.Id
                                    WHERE Latest = 1 AND {0}.ContentItemRecord_Id = {2}", GetPrefixedTableName("Routable_RoutePartRecord"), GetPrefixedTableName("Orchard_Framework_ContentItemVersionRecord"), autoroutePart.ContentItem.Id);
                                var reader = command.ExecuteReader();
                                reader.Read();

                                try {
                                    var title = reader.GetString(0);
                                    var path = reader.GetString(1);

                                    reader.Close();

                                    autoroutePart.DisplayAlias = path ?? String.Empty;
                                    titlePart.Title = title;

                                    // updating order if it's a container
                                    var containerPart = autoroutePart.As<ContainerPart>();
                                    if(containerPart != null) {
                                        if(!String.IsNullOrEmpty(containerPart.OrderByProperty) && containerPart.OrderByProperty.StartsWith("RoutePart")) {
                                            containerPart.OrderByProperty = "TitlePart.Title";
                                        }
                                    }

                                    _autorouteService.PublishAlias(autoroutePart);
                                }
                                catch(Exception e) {
                                    if (!reader.IsClosed) {
                                        reader.Close();
                                    }
                                    
                                    _reportsCoordinator.Error("Migration", "Migrating content item " + autoroutePart.ContentItem.Id + " failed with: " + e.Message);
                                    errors = true;
                                }
                            }

                            count++;
                        }

                        _orchardServices.ContentManager.Flush();
                        _orchardServices.ContentManager.Clear();

                    } while (contents.Any());
 
                    _contentDefinitionManager.AlterTypeDefinition(contentType, builder => builder.RemovePart("RoutePart"));
                    
                    var typeDefinition = _contentDefinitionManager.GetTypeDefinition(contentType);
                    if (isContainable || typeDefinition.Parts.Any(x => x.PartDefinition.Name == "ContainablePart")) {
                        _autorouteService.CreatePattern(contentType, "Container and Title", "{Content.Container.Path}/{Content.Slug}", "my-container/a-sample-title", true);
                    }
                    else {
                        _autorouteService.CreatePattern(contentType, "Title", "{Content.Slug}", "my-sample-title", true);    
                    }

                    if (errors) {
                        _orchardServices.Notifier.Warning(T("Some content items could not be imported. Please refer to the corresponding Report."));
                    }
                    else {
                        _orchardServices.Notifier.Information(T("{0} was migrated successfully", contentType));
                    }
                }
            }

            return RedirectToAction("Index");
        }

        private string GetPrefixedTableName(string tableName) {
            if (string.IsNullOrWhiteSpace(_shellSettings.DataTablePrefix)) {
                return tableName;
            }

            return _shellSettings.DataTablePrefix + "_" + tableName;
        }
    }
}
