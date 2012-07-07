﻿using JetBrains.Annotations;
using Orchard.ContentManagement;
using Orchard.Core.Navigation.Models;
using Orchard.Data;
using Orchard.ContentManagement.Handlers;

namespace Orchard.Core.Navigation.Handlers {
    [UsedImplicitly]
    public class ContentMenuItemPartHandler : ContentHandler {
        private readonly IContentManager _contentManager;

        public ContentMenuItemPartHandler(IContentManager contentManager, IRepository<ContentMenuItemPartRecord> repository) {
            _contentManager = contentManager;
            Filters.Add(new ActivatingFilter<ContentMenuItemPart>("ContentMenuItem"));
            Filters.Add(StorageFilter.For(repository));

            OnLoading<ContentMenuItemPart>((context, part) => part._content.Loader(p => contentManager.Get(part.Record.ContentMenuItemRecord.Id)));
        }

        protected override void GetItemMetadata(GetContentItemMetadataContext context) {
            base.GetItemMetadata(context);

            if (context.ContentItem.ContentType != "ContentMenuItem") {
                return;
            }

            var contentMenuItemPart = context.ContentItem.As<ContentMenuItemPart>();
            // the display route for the menu item is the one for the referenced content item
            if(contentMenuItemPart != null) {
                context.Metadata.DisplayRouteValues = _contentManager.GetItemMetadata(contentMenuItemPart.Content).DisplayRouteValues;
            }
        }
    }
}