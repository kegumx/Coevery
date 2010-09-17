using System.Web.Mvc;
using JetBrains.Annotations;
using Orchard.DisplayManagement;
using Orchard.Mvc.Filters;

namespace Orchard.Core.Feeds.Services {
    [UsedImplicitly]
    public class FeedFilter : FilterProvider, IResultFilter {
        private readonly IFeedManager _feedManager;
        private readonly IWorkContextAccessor _workContextAccessor;

        public FeedFilter(IFeedManager feedManager, IWorkContextAccessor workContextAccessor, IShapeHelperFactory shapeHelperFactory) {
            _feedManager = feedManager;
            _workContextAccessor = workContextAccessor;
            Shape = shapeHelperFactory.CreateHelper();
        }

        dynamic Shape { get; set; }

        public void OnResultExecuting(ResultExecutingContext filterContext) {
            var page  =_workContextAccessor.GetContext(filterContext).Page;
            var feed = Shape.Feed()
                .FeedManager(_feedManager);
            page.Zones.Head.Add(feed, ":after");
        }

        public void OnResultExecuted(ResultExecutedContext filterContext) {}
    }
}