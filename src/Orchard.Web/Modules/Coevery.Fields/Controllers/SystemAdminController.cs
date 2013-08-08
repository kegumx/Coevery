﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Web.Mvc;
using Coevery.Core.Services;
using Coevery.Entities;
using Coevery.Fields.Settings;
using Coevery.Fields.ViewModels;
using Orchard;
using Orchard.ContentManagement;
using Orchard.ContentManagement.MetaData;
using Orchard.ContentManagement.MetaData.Models;
using Orchard.Localization;
using Orchard.UI.Notify;
using Orchard.Utility.Extensions;
using IContentDefinitionEditorEvents = Coevery.Fields.Settings.IContentDefinitionEditorEvents;
using Coevery.Fields.Services;
using EditPartFieldViewModel = Coevery.Fields.ViewModels.EditPartFieldViewModel;
using EditPartViewModel = Coevery.Fields.ViewModels.EditPartViewModel;

namespace Coevery.Fields.Controllers {
    public class SystemAdminController : Controller, IUpdateModel {
        private readonly IContentDefinitionService _contentDefinitionService;
        private readonly IContentDefinitionManager _contentDefinitionManager;
        private readonly IContentDefinitionEditorEvents _contentDefinitionEditorEvents;
        private readonly ISchemaUpdateService _schemaUpdateService;
        private readonly IFieldService _fieldService;

        public SystemAdminController(
            IOrchardServices orchardServices,
            IContentDefinitionService contentDefinitionService,
            IContentDefinitionManager contentDefinitionManager,
            IContentDefinitionEditorEvents contentDefinitionEditorEvents,
            ISchemaUpdateService schemaUpdateService,
            IFieldService fieldService) {
            Services = orchardServices;
            _contentDefinitionService = contentDefinitionService;
            _contentDefinitionManager = contentDefinitionManager;
            _contentDefinitionEditorEvents = contentDefinitionEditorEvents;
            _schemaUpdateService = schemaUpdateService;
            _fieldService = fieldService;
            T = NullLocalizer.Instance;
        }

        public IOrchardServices Services { get; private set; }
        public Localizer T { get; set; }

        public ActionResult CreateChooseType(string id) {
            if (!Services.Authorizer.Authorize(Permissions.EditContentTypes, T("Not allowed to edit a content part.")))
                return new HttpUnauthorizedResult();

            var viewModel = new AddFieldViewModel {
                Fields = _contentDefinitionService.GetFields().OrderBy(x => x.FieldTypeName),
            };

            return View(viewModel);
        }

        public ActionResult FieldName(string entityName, string displayName, int version) {
            return Json(new {
                result = _contentDefinitionService.GenerateFieldNameFromDisplayName(entityName, displayName),
                version = version
            });
        }

        public ActionResult CreateEditInfo(string id, string fieldTypeName) {
            if (!Services.Authorizer.Authorize(Permissions.EditContentTypes, T("Not allowed to edit a content part.")))
                return new HttpUnauthorizedResult();
            var ContentFieldDefinition = new ContentFieldDefinition(fieldTypeName + "Create");
            ContentFieldDefinition.ContentType = id;
            var definition = new ContentPartFieldDefinition(ContentFieldDefinition, string.Empty, new SettingsDictionary());
            var templates = _contentDefinitionEditorEvents.PartFieldEditor(definition);

            var viewModel = new AddFieldViewModel {
                FieldTypeName = fieldTypeName,
                TypeTemplates = templates,
                AddInLayout = true
            };

            return View(viewModel);
        }

        [HttpPost, ActionName("CreateEditInfo")]
        public ActionResult CreateEditInfoPost(string id, AddFieldViewModel viewModel) {
            if (!Services.Authorizer.Authorize(Permissions.EditContentTypes, T("Not allowed to edit a content part.")))
                return new HttpUnauthorizedResult();

            var partViewModel = _contentDefinitionService.GetPart(id);
            var typeViewModel = _contentDefinitionService.GetType(id);

            if (partViewModel == null) {
                // id passed in might be that of a type w/ no implicit field
                if (typeViewModel != null) {
                    partViewModel = new EditPartViewModel { Name = typeViewModel.Name };
                    _contentDefinitionService.AddPart(new CreatePartViewModel { Name = partViewModel.Name });
                    _contentDefinitionService.AddPartToType(partViewModel.Name, typeViewModel.Name);
                }
                else {
                    return HttpNotFound();
                }
            }

            _fieldService.Create(id, viewModel, this);

            if (!ModelState.IsValid) {
                Services.TransactionManager.Cancel();
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                var temp = (from values in ModelState
                            from error in values.Value.Errors
                            select error.ErrorMessage).ToArray();
                return Content(string.Concat(temp));
            }

            Services.Notifier.Information(T("The \"{0}\" field has been added.", viewModel.DisplayName));
            _schemaUpdateService.CreateColumn(id, viewModel.Name, viewModel.FieldTypeName);
            return new HttpStatusCodeResult(HttpStatusCode.OK);
        }

        public ActionResult Edit(string id, string fieldName) {
            if (!Services.Authorizer.Authorize(Permissions.EditContentTypes, T("Not allowed to edit a content type.")))
                return new HttpUnauthorizedResult();

            var typeViewModel = _contentDefinitionService.GetType(id);
            if (typeViewModel == null) {
                return HttpNotFound();
            }

            var fieldViewModel = typeViewModel.Fields.FirstOrDefault(x => x.Name == fieldName);

            if (fieldViewModel == null) {
                return HttpNotFound();
            }

            return View(fieldViewModel);
        }

        [HttpPost, ActionName("Edit")]
        public ActionResult EditPost(EditPartFieldViewModel viewModel, string id) {
            if (!Services.Authorizer.Authorize(Permissions.EditContentTypes, T("Not allowed to edit a content type.")))
                return new HttpUnauthorizedResult();

            if (viewModel == null)
                return HttpNotFound();

            var partViewModel = _contentDefinitionService.GetPart(id);
            if (partViewModel == null) {
                return HttpNotFound();
            }

            // prevent null reference exception in validation
            viewModel.DisplayName = viewModel.DisplayName ?? String.Empty;

            // remove extra spaces
            viewModel.DisplayName = viewModel.DisplayName.Trim();

            if (String.IsNullOrWhiteSpace(viewModel.DisplayName)) {
                ModelState.AddModelError("DisplayName", T("The Display Name name can't be empty.").ToString());
            }

            if (partViewModel.Fields.Any(t => t.Name != viewModel.Name && String.Equals(t.DisplayName.Trim(), viewModel.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))) {
                ModelState.AddModelError("DisplayName", T("A field with the same Display Name already exists.").ToString());
            }

            var fieldDefinition = _contentDefinitionManager.GetPartDefinition(id).Fields.FirstOrDefault(f => f.Name == viewModel.Name);

            if (fieldDefinition == null) {
                return HttpNotFound();
            }

            var typeViewModel = _contentDefinitionService.GetType(id);
            var field = typeViewModel.Fields.FirstOrDefault(f => f.Name == viewModel.Name);
            CheckData(field);
            if (!ModelState.IsValid) {
                Services.TransactionManager.Cancel();
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                var temp = (from values in ModelState
                            from error in values.Value.Errors
                            select error.ErrorMessage).ToArray();
                return Content(string.Concat(temp));
            }

            fieldDefinition.DisplayName = viewModel.DisplayName;
            _contentDefinitionManager.StorePartDefinition(partViewModel._Definition);

            _contentDefinitionService.AlterField(id, viewModel, this);
            if (!ModelState.IsValid) {
                Services.TransactionManager.Cancel();
                Response.StatusCode = (int)HttpStatusCode.BadRequest;
                var temp = (from values in ModelState
                            from error in values.Value.Errors
                            select error.ErrorMessage).ToArray();
                return Content(string.Concat(temp));
            }

            _schemaUpdateService.CreateColumn(partViewModel.Name, field.Name, field.FieldDefinition.Name);
            return new HttpStatusCodeResult(HttpStatusCode.OK);
        }

        public ActionResult Items(string id, string fieldName) {
            return View();
        }

        public ActionResult DependencyList(string id) {
            return View();
        }

        public ActionResult CreateDependency(string id) {
            var typeViewModel = _contentDefinitionService.GetType(id);
            var controlFields = new List<EditPartFieldViewModel>();
            var dependentFields = new List<EditPartFieldViewModel>();
            foreach (var field in typeViewModel.Fields) {
                switch (field.FieldDefinition.Name) {
                    case "SelectField":
                        controlFields.Add(field);
                        dependentFields.Add(field);
                        break;
                    case "BooleanField":
                        controlFields.Add(field);
                        break;
                }
            }
            var viewModel = new FieldDependencyViewModel {
                ControlFields = controlFields,
                DependentFields = dependentFields
            };
            return View(viewModel);
        }

        public ActionResult EditDependency(string entityName, int itemId) {
            var typeViewModel = _contentDefinitionService.GetType(entityName);

            return View();
        }

        bool IUpdateModel.TryUpdateModel<TModel>(TModel model, string prefix, string[] includeProperties, string[] excludeProperties) {
            return base.TryUpdateModel(model, prefix, includeProperties, excludeProperties);
        }

        void IUpdateModel.AddModelError(string key, LocalizedString errorMessage) {
            ModelState.AddModelError(key, errorMessage.ToString());
        }

        private void CheckData(EditPartFieldViewModel serverField) {
            var settingsStr = serverField.FieldDefinition.Name + "Settings";
            var clientSettings = new FieldSettings();
            TryUpdateModel(clientSettings, settingsStr);
            clientSettings.ReadOnly = false;

            var serverSettings = new FieldSettings {
                IsSystemField = bool.Parse(serverField.Settings[settingsStr + ".IsSystemField"]),
                Required = bool.Parse(serverField.Settings[settingsStr + ".Required"]),
                ReadOnly = bool.Parse(serverField.Settings[settingsStr + ".ReadOnly"]),
                AlwaysInLayout = bool.Parse(serverField.Settings[settingsStr + ".AlwaysInLayout"])
            };

            if (clientSettings.ReadOnly) {
                ModelState.AddModelError("ReadOnly", T("Can't modify the ReadOnly field.").ToString());
            }

            if (clientSettings.IsSystemField != serverSettings.IsSystemField) {
                ModelState.AddModelError("IsSystemField", T("Can't modify the IsSystemField field.").ToString());
            }

            if (serverSettings.IsSystemField) {
                if (clientSettings.Required != serverSettings.Required) {
                    ModelState.AddModelError("Required", T("Can't modify the Required field.").ToString());
                }
                if (clientSettings.ReadOnly != serverSettings.ReadOnly) {
                    ModelState.AddModelError("ReadOnly", T("Can't modify the ReadOnly field.").ToString());
                }
                if (clientSettings.AlwaysInLayout != serverSettings.AlwaysInLayout) {
                    ModelState.AddModelError("AlwaysInLayout", T("Can't modify the AlwaysInLayout field.").ToString());
                }
            }
        }
    }
}