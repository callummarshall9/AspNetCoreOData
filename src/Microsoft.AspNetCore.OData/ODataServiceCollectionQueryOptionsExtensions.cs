//-----------------------------------------------------------------------------
// <copyright file="EnableQueryAttribute.cs" company=".NET Foundation">
//      Copyright (c) .NET Foundation and Contributors. All rights reserved.
//      See License.txt in the project root for license information.
// </copyright>
//------------------------------------------------------------------------------

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OData.Common;
using Microsoft.AspNetCore.OData.Edm;
using Microsoft.AspNetCore.OData.Extensions;
using Microsoft.AspNetCore.OData.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData;
using Microsoft.OData.Edm;
using Microsoft.OData.UriParser;
using System;
using System.Collections;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Microsoft.AspNetCore.OData.Query
{
    /// <summary>
    /// This class defines an attribute that can be applied to an action to enable querying using the OData query
    /// syntax. To avoid processing unexpected or malicious queries, use the validation settings on
    /// <see cref="ODataServiceCollectionQueryOptionsExtensions"/> to validate incoming queries.
    /// </summary>
    public partial class ODataServiceCollectionQueryOptionsExtensions
    {
        /// <summary>
        /// Creates the <see cref="ODataQueryOptions"/> for action executing validation.
        /// </summary>
        /// <param name="actionExecutingContext">The action executing context.</param>
        /// <returns>The created <see cref="ODataQueryOptions"/> or null if we can't create it during action executing.</returns>
        public virtual ODataQueryOptions CreateQueryOptionsOnExecuting(IServiceProvider serviceProvider)
        {
            var httpContextAccessor = serviceProvider.GetService(typeof(IHttpContextAccessor)) as IHttpContextAccessor;

            var httpContext = httpContextAccessor.HttpContext;

            HttpRequest request = httpContext.Request;
            ODataPath path = request.ODataFeature().Path;

            _querySettings.TimeZone = request.GetTimeZoneInfo();

            ODataQueryContext queryContext;

            // For OData based controllers.
            if (path != null)
            {
                IEdmType edmType = path.GetEdmType();

                // When $count is at the end, the return type is always int. Trying to instead fetch the return type of the actual type being counted on.
                if (request.IsCountRequest())
                {
                    ODataPathSegment[] pathSegments = path.ToArray();
                    edmType = pathSegments[pathSegments.Length - 2].EdmType;
                }

                IEdmType elementType = edmType.AsElementType();
                if (elementType.IsUntyped())
                {
                    // TODO: so far, we don't know how to process query on Edm.Untyped.
                    // So, if the query data type is Edm.Untyped, or collection of Edm.Untyped,
                    // Let's simply skip it now.
                    return null;
                }

                IEdmModel edmModel = request.GetModel();

                // For Swagger metadata request. elementType is null.
                if (elementType == null || edmModel == null)
                {
                    return null;
                }

                Type clrType = edmModel.GetClrType(elementType.ToEdmTypeReference(isNullable: false));

                // CLRType can be missing if untyped registrations were made.
                if (clrType != null)
                {
                    queryContext = new ODataQueryContext(edmModel, clrType, path);
                }
                else
                {
                    // In case where CLRType is missing, $count, $expand verifications cannot be done.
                    // More importantly $expand required ODataQueryContext with clrType which cannot be done
                    // If the model is untyped. Hence for such cases, letting the validation run post action.
                    return null;
                }
            }
            else
            {
                return null;
            }

            // Create and validate the query options.
            return new ODataQueryOptions(queryContext, request);
        }


        /// <summary>
        /// Create and validate a new instance of <see cref="ODataQueryOptions"/> from a query and context during action executed.
        /// Developers can override this virtual method to provide its own <see cref="ODataQueryOptions"/>.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="queryContext">The query context.</param>
        /// <returns>The created <see cref="ODataQueryOptions"/>.</returns>
        public virtual ODataQueryOptions<T> CreateAndValidateQueryOptions<T>(HttpRequest request, ODataQueryContext queryContext) where T : class
        {
            if (request == null)
            {
                throw Error.ArgumentNull("request");
            }

            if (queryContext == null)
            {
                throw Error.ArgumentNull("queryContext");
            }

            RequestQueryData requestQueryData = request.HttpContext.Items[nameof(RequestQueryData)] as RequestQueryData;

            if (requestQueryData != null && requestQueryData.QueryValidationRunBeforeActionExecution)
            {
                // processed, just return the query option and skip validation.
                return requestQueryData.ProcessedQueryOptions as ODataQueryOptions<T>;
            }

            ODataQueryOptions<T> queryOptions = new ODataQueryOptions<T>(queryContext, request);

            ValidateQuery(request, queryOptions);

            return queryOptions;
        }

        /// <summary>
        /// Get a single or default value from a collection.
        /// </summary>
        /// <param name="queryable">The response value as <see cref="IQueryable"/>.</param>
        /// <param name="actionDescriptor">The action context, i.e. action and controller name.</param>
        /// <returns></returns>
        internal static object SingleOrDefault(
            IQueryable queryable,
            ControllerActionDescriptor actionDescriptor)
        {
            var enumerator = queryable.GetEnumerator();
            try
            {
                var result = enumerator.MoveNext() ? enumerator.Current : null;

                if (enumerator.MoveNext())
                {
                    throw new InvalidOperationException(Error.Format(
                        SRResources.SingleResultHasMoreThanOneEntity,
                        actionDescriptor.ActionName,
                        actionDescriptor.ControllerName,
                        "SingleResult"));
                }

                return result;
            }
            finally
            {
                // Ensure any active/open database objects that were created
                // iterating over the IQueryable object are properly closed.
                var disposable = enumerator as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
        }

        /// <summary>
        /// Validate the select and expand options.
        /// </summary>
        /// <param name="queryOptions">The query options.</param>
        internal static void ValidateSelectExpandOnly(ODataQueryOptions queryOptions)
        {
            if (queryOptions.Filter != null || queryOptions.Count != null || queryOptions.OrderBy != null
                || queryOptions.Skip != null || queryOptions.Top != null)
            {
                throw new ODataException(Error.Format(SRResources.NonSelectExpandOnSingleEntity));
            }
        }

        /// <summary>
        /// Get the ODaya query context.
        /// </summary>
        /// <param name="responseValue">The response value.</param>
        /// <param name="singleResultCollection">The content as SingleResult.Queryable.</param>
        /// <param name="actionDescriptor">The action context, i.e. action and controller name.</param>
        /// <param name="request">The OData path.</param>
        /// <returns></returns>
        private ODataQueryContext GetODataQueryContext(
            object responseValue,
            IQueryable singleResultCollection,
            ControllerActionDescriptor actionDescriptor,
            HttpRequest request)
        {
            Type elementClrType = GetElementType(responseValue, singleResultCollection, actionDescriptor);

            IEdmModel model = GetModel(elementClrType, request, actionDescriptor);
            if (model == null)
            {
                throw Error.InvalidOperation(SRResources.QueryGetModelMustNotReturnNull);
            }

            return new ODataQueryContext(model, elementClrType, request.ODataFeature().Path);
        }

        /// <summary>
        /// Get the element type.
        /// </summary>
        /// <param name="responseValue">The response value.</param>
        /// <param name="singleResultCollection">The content as SingleResult.Queryable.</param>
        /// <param name="actionDescriptor">The action context, i.e. action and controller name.</param>
        /// <returns></returns>
        internal static Type GetElementType(
            object responseValue,
            IQueryable singleResultCollection,
            ControllerActionDescriptor actionDescriptor)
        {
            Contract.Assert(responseValue != null);

            IEnumerable enumerable = responseValue as IEnumerable;
            if (enumerable == null)
            {
                if (singleResultCollection == null)
                {
                    return responseValue.GetType();
                }

                enumerable = singleResultCollection;
            }

            Type elementClrType = TypeHelper.GetImplementedIEnumerableType(enumerable.GetType());
            if (elementClrType == null)
            {
                // The element type cannot be determined because the type of the content
                // is not IEnumerable<T> or IQueryable<T>.
                throw Error.InvalidOperation(
                    SRResources.FailedToRetrieveTypeToBuildEdmModel,
                    typeof(ODataServiceCollectionQueryOptionsExtensions).Name,
                    actionDescriptor.ActionName,
                    actionDescriptor.ControllerName,
                    responseValue.GetType().FullName);
            }

            return elementClrType;
        }

        /// <summary>
        /// Validates the OData query in the incoming request. By default, the implementation throws an exception if
        /// the query contains unsupported query parameters. Override this method to perform additional validation of
        /// the query.
        /// </summary>
        /// <param name="request">The incoming request.</param>
        /// <param name="queryOptions">
        /// The <see cref="ODataQueryOptions"/> instance constructed based on the incoming request.
        /// </param>
        public virtual void ValidateQuery(HttpRequest request, ODataQueryOptions queryOptions)
        {
            if (request == null)
            {
                throw Error.ArgumentNull(nameof(request));
            }

            if (queryOptions == null)
            {
                throw Error.ArgumentNull(nameof(queryOptions));
            }

            IQueryCollection queryParameters = request.Query;
            foreach (var kvp in queryParameters)
            {
                if (!queryOptions.IsSupportedQueryOption(kvp.Key) &&
                     kvp.Key.StartsWith("$", StringComparison.Ordinal))
                {
                    // we don't support any custom query options that start with $
                    // this should be caught be OnActionExecuted().
                    throw new ODataException(Error.Format(SRResources.CustomQueryOptionNotSupportedWithDollarSign, kvp.Key));
                }
            }

            queryOptions.Validate(_validationSettings);
        }

        /// <summary>
        /// Determine if the query contains auto select and expand property.
        /// </summary>
        /// <param name="responseValue">The response value.</param>
        /// <param name="singleResultCollection">The content as SingleResult.Queryable.</param>
        /// <param name="actionDescriptor">The action context, i.e. action and controller name.</param>
        /// <param name="request">The Http request.</param>
        /// <returns>true/false</returns>
        private bool ContainsAutoSelectExpandProperty(object responseValue, IQueryable singleResultCollection,
            ControllerActionDescriptor actionDescriptor, HttpRequest request)
        {
            Type elementClrType = GetElementType(responseValue, singleResultCollection, actionDescriptor);

            IEdmModel model = GetModel(elementClrType, request, actionDescriptor);
            if (model == null)
            {
                throw Error.InvalidOperation(SRResources.QueryGetModelMustNotReturnNull);
            }
            IEdmType edmType = model.GetEdmTypeReference(elementClrType)?.Definition;

            IEdmStructuredType structuredType = edmType as IEdmStructuredType;
            ODataPath path = request.ODataFeature().Path;

            IEdmProperty pathProperty = null;
            IEdmStructuredType pathStructuredType = null;
            if (path != null)
            {
                (pathProperty, pathStructuredType, _) = path.GetPropertyAndStructuredTypeFromPath();
            }

            // Take the type and property from path first, it's higher priority than the value type.
            if (pathStructuredType != null && pathProperty != null)
            {
                return model.HasAutoExpandProperty(pathStructuredType, pathProperty) || model.HasAutoSelectProperty(pathStructuredType, pathProperty);
            }
            else if (structuredType != null)
            {
                return model.HasAutoExpandProperty(structuredType, null) || model.HasAutoSelectProperty(structuredType, null);
            }

            return false;
        }

        /// <summary>
        /// Gets the EDM model for the given type and request.Override this method to customize the EDM model used for
        /// querying.
        /// </summary>
        /// <param name="elementClrType">The CLR type to retrieve a model for.</param>
        /// <param name="request">The request message to retrieve a model for.</param>
        /// <param name="actionDescriptor">The action descriptor for the action being queried on.</param>
        /// <returns>The EDM model for the given type and request.</returns>
        public virtual IEdmModel GetModel(
            Type elementClrType,
            HttpRequest request,
            ActionDescriptor actionDescriptor)
        {
            // Get model for the request
            IEdmModel model = request.GetModel();

            if (model == null ||
                model == EdmCoreModel.Instance || model.GetEdmType(elementClrType) == null)
            {
                // user has not configured anything or has registered a model without the element type
                // let's create one just for this type and cache it in the action descriptor
                model = actionDescriptor.GetEdmModel(request, elementClrType);
            }

            Contract.Assert(model != null);
            return model;
        }

        /// <summary>
        /// Holds request level query information.
        /// </summary>
        private class RequestQueryData
        {
            /// <summary>
            /// Gets or sets a value indicating whether query validation was run before action (controller method) is executed.
            /// </summary>
            /// <remarks>
            /// Marks if the query validation was run before the action execution. This is not always possible.
            /// For cases where the run failed before action execution. We will run validation on result.
            /// </remarks>
            public bool QueryValidationRunBeforeActionExecution { get; set; }

            /// <summary>
            /// Gets or sets the processed query options.
            /// </summary>
            /// <remarks>
            /// Stores the processed query options to be used later if OnActionExecuting was able to verify the query.
            /// This is because ValidateQuery internally modifies query options (expands are prime example of this).
            /// </remarks>
            public ODataQueryOptions ProcessedQueryOptions { get; set; }
        }
    }
}
