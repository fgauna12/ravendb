﻿// -----------------------------------------------------------------------
//  <copyright file="a.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http.Controllers;
using System.Web.Http.Routing;
using Raven.Abstractions;
using Raven.Abstractions.Logging;
using Raven.Database.Config;
using Raven.Database.Server;
using Raven.Database.Server.Controllers;
using Raven.Database.Server.Security;
using Raven.Database.Server.Tenancy;
using Raven.Database.Server.WebApi;

namespace Raven.Database.TimeSeries.Controllers
{
	public abstract class RavenTimeSeriesApiController : RavenBaseApiController
	{
		private static readonly ILog Logger = LogManager.GetCurrentClassLogger();

		private PagingInfo paging;
		private NameValueCollection queryString;

		private TimeSeriesLandlord landlord;
		private RequestManager requestManager;
        
		new public RequestManager RequestManager
		{
			get
			{
				if (Configuration == null)
					return requestManager;
				return (RequestManager)Configuration.Properties[typeof(RequestManager)];
			}
		}

	    public TimeSeriesStorage TimeSeries
	    {
	        get
	        {
		        if (string.IsNullOrWhiteSpace(TimeSeriesName))
			        throw new InvalidOperationException("Could not find time series name in path.. maybe it is missing or the request URL is malformed?");

		        var timeSeries = TimeSeriesLandlord.GetTimeSeriesInternal(TimeSeriesName);
                if (timeSeries == null)
                {
                    throw new InvalidOperationException("Could not find a time series named: " + TimeSeriesName);
                }

                return timeSeries.Result;
	        }
	    }

		public override async Task<HttpResponseMessage> ExecuteAsync(HttpControllerContext controllerContext, CancellationToken cancellationToken)
		{
			InnerInitialization(controllerContext);
			var authorizer = (MixedModeRequestAuthorizer)controllerContext.Configuration.Properties[typeof(MixedModeRequestAuthorizer)];
			var result = new HttpResponseMessage();
			if (InnerRequest.Method.Method != "OPTIONS")
			{
				result = await RequestManager.HandleActualRequest(this, controllerContext, async () =>
				{
					RequestManager.SetThreadLocalState(ReadInnerHeaders, TimeSeriesName);
					return await ExecuteActualRequest(controllerContext, cancellationToken, authorizer);
				}, httpException => GetMessageWithObject(new { Error = httpException.Message }, HttpStatusCode.ServiceUnavailable));
			}

			RequestManager.AddAccessControlHeaders(this, result);
			RequestManager.ResetThreadLocalState();

			return result;
		}

		private async Task<HttpResponseMessage> ExecuteActualRequest(HttpControllerContext controllerContext, CancellationToken cancellationToken,
			MixedModeRequestAuthorizer authorizer)
		{
			HttpResponseMessage authMsg;
			if (authorizer.TryAuthorize(this, out authMsg) == false)
				return authMsg;

            if (IsInternalRequest == false)
				RequestManager.IncrementRequestCount();

			var fileSystemInternal = await TimeSeriesLandlord.GetTimeSeriesInternal(TimeSeriesName);
			if (fileSystemInternal == null)
			{
				var msg = "Could not find a time series named: " + TimeSeriesName;
				return GetMessageWithObject(new { Error = msg }, HttpStatusCode.ServiceUnavailable);
			}

			var sp = Stopwatch.StartNew();

			var result = await base.ExecuteAsync(controllerContext, cancellationToken);
			sp.Stop();
			AddRavenHeader(result, sp);

			return result;
		}

		protected override void InnerInitialization(HttpControllerContext controllerContext)
		{
			base.InnerInitialization(controllerContext);
			landlord = (TimeSeriesLandlord)controllerContext.Configuration.Properties[typeof(TimeSeriesLandlord)];
			requestManager = (RequestManager)controllerContext.Configuration.Properties[typeof(RequestManager)];

			var values = controllerContext.Request.GetRouteData().Values;
			if (values.ContainsKey("MS_SubRoutes"))
			{
				var routeDatas = (IHttpRouteData[])controllerContext.Request.GetRouteData().Values["MS_SubRoutes"];
				var selectedData = routeDatas.FirstOrDefault(data => data.Values.ContainsKey("timeSeriesName"));

				if (selectedData != null)
					TimeSeriesName = selectedData.Values["timeSeriesName"] as string;
			}
			else
			{
				if (values.ContainsKey("timeSeriesName"))
					TimeSeriesName = values["timeSeriesName"] as string;
			}
		}

		public string TimeSeriesName { get; private set; }

		private NameValueCollection QueryString
		{
			get { return queryString ?? (queryString = HttpUtility.ParseQueryString(Request.RequestUri.Query)); }
		}

		protected PagingInfo Paging
		{
			get
			{
				if (paging != null)
					return paging;

				int start;
				int.TryParse(QueryString["start"], out start);

				int pageSize;
				if (int.TryParse(QueryString["pageSize"], out pageSize) == false)
					pageSize = 25;

				paging = new PagingInfo
				{
					PageSize = Math.Min(1024, Math.Max(1, pageSize)),
					Start = Math.Max(start, 0)
				};

				return paging;
			}
		}

		protected class PagingInfo
		{
			public int PageSize;
			public int Start;
		}

	    public override InMemoryRavenConfiguration ResourceConfiguration
	    {
	        get { return TimeSeries.Configuration; }
	    }

		public override async Task<RequestWebApiEventArgs> TrySetupRequestToProperResource()
		{
			var tenantId = TimeSeriesName;
			if (string.IsNullOrWhiteSpace(tenantId))
				throw new HttpException(503, "Could not find a time series with no name");

			Task<TimeSeriesStorage> resourceStoreTask;
			bool hasTimeSeries;
			string msg;
			try
			{
				hasTimeSeries = landlord.TryGetOrCreateResourceStore(tenantId, out resourceStoreTask);
			}
			catch (Exception e)
			{
				msg = "Could not open time series named: " + tenantId;
				Logger.WarnException(msg, e);
				throw new HttpException(503, msg, e);
			}
			if (hasTimeSeries)
			{
				try
				{
					if (await Task.WhenAny(resourceStoreTask, Task.Delay(TimeSpan.FromSeconds(30))) != resourceStoreTask)
					{
						msg = "The time series " + tenantId +
								  " is currently being loaded, but after 30 seconds, this request has been aborted. Please try again later, file system loading continues.";
						Logger.Warn(msg);
						throw new HttpException(503, msg);
					}

					landlord.LastRecentlyUsed.AddOrUpdate(tenantId, SystemTime.UtcNow, (s, time) => SystemTime.UtcNow);

					return new RequestWebApiEventArgs
					{
						Controller = this,
						IgnoreRequest = false,
						TenantId = tenantId,
						TimeSeries = resourceStoreTask.Result
					};
				}
				catch (Exception e)
				{
					msg = "Could open time series named: " + tenantId;
					Logger.WarnException(msg, e);
					throw new HttpException(503, msg, e);
				}
			}

			msg = "Could not find a time series named: " + tenantId;
			Logger.Warn(msg);
			throw new HttpException(503, msg);
		}

		public override string TenantName
		{
			get { return TenantNamePrefix + TimeSeriesName; }
		}

		private const string TenantNamePrefix = "ts/";

		public override void MarkRequestDuration(long duration)
        {
            if (Storage == null)
                return;
            // TODO: Storage.MetricsTimeSeries.RequestDurationMetric.Update(duration);
        }

		public override InMemoryRavenConfiguration SystemConfiguration
		{
			get { return TimeSeriesLandlord.SystemConfiguration; }
		}

		public TimeSeriesStorage Storage
		{
			get
			{
				var timeSeries = TimeSeriesLandlord.GetTimeSeriesInternal(TimeSeriesName);
				if (timeSeries == null)
				{
					throw new InvalidOperationException("Could not find a time series named: " + TimeSeriesName);
				}

				return timeSeries.Result;
			}
		}
	}
}