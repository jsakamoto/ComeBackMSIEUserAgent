using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web;

namespace Toolbelt.Web
{
    public class ComeBackMSIEUserAgentModule : IHttpModule
    {
        public void Init(HttpApplication context)
        {
            var collectionParam = ParamOf<NameValueCollection>();
            var boolParam = ParamOf<bool>();
            var stringParam = ParamOf<string>();
            var objectParam = ParamOf<object>();

            // for IIS7 or higher with integration mode.
            var collectionType = typeof(NameValueCollection);
            var readOnlyProperty = collectionType.GetProperty("IsReadOnly", BindingFlags.NonPublic | BindingFlags.Instance);
            if (readOnlyProperty == null) throw new InvalidOperationException(string.Format("Could not find property '{0}' on type '{1}'", "IsReadOnly", collectionType));
            var isReadOnly = Expression.Lambda<Func<NameValueCollection, bool>>(
                    Expression.Property(collectionParam, readOnlyProperty),
                    collectionParam
                    ).Compile();
            var setReadOnly = Expression.Lambda<Action<NameValueCollection, bool>>(
                    Expression.Call(collectionParam, readOnlyProperty.GetSetMethod(true), boolParam),
                    collectionParam, boolParam
                    ).Compile();

            // for IIS6 or classic mode.
            var svrVarsColEntryType = GetNonPublicType<HttpContext>("System.Web.HttpServerVarsCollectionEntry");
            var svrVarsCollectionType = GetNonPublicType<HttpContext>("System.Web.HttpServerVarsCollection");
            var svrVarsParam = ParamOf(svrVarsCollectionType);

            var baseSetMethod = GetNonPublicMethod(collectionType,"BaseSet", typeof(string), typeof(object));
            var baseSet = Expression.Lambda<Action<NameValueCollection, string, object>>(
                    Expression.Call(collectionParam, baseSetMethod, stringParam, objectParam),
                    collectionParam, stringParam, objectParam
                    ).Compile();

            var populateMethod = GetNonPublicMethod(svrVarsCollectionType, "Populate");
            var invalidateCachedArraysMethod = GetNonPublicMethod(svrVarsCollectionType, "InvalidateCachedArrays");
            var synchronizeHeaderMethod = GetNonPublicMethod(svrVarsCollectionType, "SynchronizeHeader");

            var httpRequestParam = ParamOf<HttpRequest>();
            var invalidateParamsMathod = GetNonPublicMethod<HttpRequest>("InvalidateParams");
            var invalidateParams = Expression.Lambda<Action<HttpRequest>>(
                    Expression.Call(httpRequestParam, invalidateParamsMathod),
                    httpRequestParam
                    ).Compile();

            var ISAPIWrkrReqType = GetNonPublicType<HttpWorkerRequest>("System.Web.Hosting.ISAPIWorkerRequest");
            var knownReqHdrsField = ISAPIWrkrReqType.GetField("_knownRequestHeaders", BindingFlags.Instance | BindingFlags.NonPublic);

            // Detect IIS version and mode.
            var isIIS7IntegratedMode = 
                GetCurrentWorkerRequest()
                .GetType()
                .Name
                .StartsWith("ISAPIWorkerRequest") == false;

            Action<NameValueCollection, string> changeUserAgent = null;
            if (isIIS7IntegratedMode)
            {
                changeUserAgent = (serverVariables, newUserAgent) => {
                    var wasReadOnly = isReadOnly(serverVariables);
                    if (wasReadOnly) setReadOnly(serverVariables, false);
                    serverVariables.Set("HTTP_USER_AGENT", newUserAgent);
                    if (wasReadOnly) setReadOnly(serverVariables, true);
                };
            }
            else
            {
                changeUserAgent = (serverVariables, newUserAgent) =>
                {
                    var request = HttpContext.Current.Request;
                    var reqHdWasReadOnly = isReadOnly(request.Headers);
                    if (reqHdWasReadOnly) setReadOnly(request.Headers, false);

                    populateMethod.Invoke(serverVariables, args());

                    var svrVarsWasReadOnly = isReadOnly(serverVariables);
                    if (svrVarsWasReadOnly) setReadOnly(serverVariables, false);

                    invalidateCachedArraysMethod.Invoke(serverVariables, args());
                    var entry = Activator.CreateInstance(svrVarsColEntryType, BindingFlags.CreateInstance | BindingFlags.NonPublic | BindingFlags.Instance, null,
                        args("HTTP_USER_AGENT", newUserAgent), null);
                    baseSet(serverVariables, "HTTP_USER_AGENT", entry);

                    synchronizeHeaderMethod.Invoke(serverVariables, args("HTTP_USER_AGENT", newUserAgent));
                    invalidateParams(request);

                    var headers = knownReqHdrsField.GetValue(GetCurrentWorkerRequest()) as string[];
                    headers[39] = newUserAgent;

                    if (svrVarsWasReadOnly) setReadOnly(serverVariables, true);
                    if (reqHdWasReadOnly) setReadOnly(request.Headers, true);
                };
            }

            context.BeginRequest += (sender, args) =>
            {
                var serverVariables = HttpContext.Current.Request.ServerVariables;
                var userAgent = (serverVariables["HTTP_USER_AGENT"] ?? string.Empty);

                if (userAgent.IndexOf("Trident/") != -1 &&
                    userAgent.IndexOf("MSIE ") == -1)
                {
                    var rev = Regex.Match(userAgent, @"[^a-z0-9]+rv:([\d\.]+)");
                    if (rev.Success)
                    {
                        var lastBlacketPos = userAgent.IndexOf('(');
                        var newUserAgent =
                            userAgent.Substring(0, lastBlacketPos + 1) +
                            "compatible; MSIE " +
                            rev.Groups[1].Value + "; " +
                            userAgent.Substring(lastBlacketPos + 1);

                        changeUserAgent(serverVariables, newUserAgent);
                    }
                }
            };
        }

        public void Dispose()
        {
        }

        private static object GetCurrentWorkerRequest()
        {
            var workerRequest = (HttpContext.Current as IServiceProvider).GetService(typeof(HttpWorkerRequest));
            return workerRequest;
        }

        private static ParameterExpression ParamOf<T>()
        {
            return Expression.Parameter(typeof(T), null);
        }

        private static ParameterExpression ParamOf(Type type)
        {
            return Expression.Parameter(type, null);
        }

        private static Type GetNonPublicType<T>(string fullName)
        {
            return GetNonPublicType(typeof(T), fullName);
        }

        private static Type GetNonPublicType(Type jumpstart, string fullName)
        {
            var type = jumpstart.Assembly.GetType(fullName, throwOnError: false);
            if (type == null) throw new InvalidOperationException(string.Format("Could not find type '{0}'", fullName));
            return type;
        }

        private static MethodInfo GetNonPublicMethod<T>(string methodName, params Type[] argumentTypes)
        {
            return GetNonPublicMethod(typeof(T), methodName, argumentTypes);
        }

        private static MethodInfo GetNonPublicMethod(Type type, string methodName, params Type[] argumentTypes)
        {
            var bindingFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            var method = argumentTypes.Length == 0 ?
                type.GetMethod(methodName, bindingFlags) :
                type.GetMethod(methodName, bindingFlags, null, argumentTypes, null);

            if (method == null) throw new InvalidOperationException(string.Format("Could not find method '{0}' on type '{1}'", methodName, type));
            return method;
        }

        private static object[] args(params object[] args)
        {
            return args;
        }
    }
}
