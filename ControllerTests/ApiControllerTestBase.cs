using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Web.Http;
using Autofac;
using Autofac.Integration.WebApi;
using Newtonsoft.Json;
using NSubstitute;

namespace ControllerTests
{
    public abstract class ApiControllerTestBase : ApiControllerTestBase<NoSession>
    {
        protected ApiControllerTestBase(ApiTestSetup setup)
            : base(new ApiTestSetup<NoSession>(setup))
        { }
    }
    public class NoSession { }      // todo: should this be internal?

    public abstract class ApiControllerTestBase<TSession> : IDisposable
    {
        private bool _disposed;
        private Lazy<HttpServer> _httpServer;
        private const string MediaType = "application/json";
        private TSession _session;
        private ApiTestSetup<TSession> _setup;

        protected ApiControllerTestBase(ApiTestSetup<TSession> setup)
        {
            Init(setup);
        }

        protected void Init(ApiTestSetup<TSession> setup)
        {
            if (setup == null)
                throw new ArgumentException("Please initialise the test class by creating a constructor and passing the setup argument to base()", "setup");
            _setup = setup;

            _httpServer = new Lazy<HttpServer>(() =>
            {
                var rootScope = setup.Container.BeginLifetimeScope(setup.AdditionalConfig);

                var config = new HttpConfiguration { DependencyResolver = new AutofacWebApiDependencyResolver(rootScope) };
                setup.RegisterWebApiConfig(config);
                config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;

                if (typeof(TSession) != typeof(NoSession))
                {
                    _session = rootScope.Resolve<TSession>();
                    if (setup.SessionSetup != null)
                        setup.SessionSetup(_session);
                }

                return new HttpServer(config);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        protected void ConfigureServices(Action<ContainerBuilder> config)
        {
            if (_httpServer.IsValueCreated)
                throw Constants.BadBuilderConfigOrderException();

            _setup.AdditionalConfig += config;
        }

        protected T SubstituteAndConfigure<T>() where T : class
        {
            var sub = Substitute.For<T>();
            ConfigureServices(builder => builder.Register(context => sub).As<T>());
            return sub;
        }

        protected TSession Session
        {
            get
            {
                if (!_httpServer.IsValueCreated)
                {
                    var initServer = _httpServer.Value;
                }

                return _session;
            }
            set { _session = value; }
        }

        protected HttpResponseMessage Get(string relativeUrl, bool useHttps = false, params Tuple<string, string>[] additionalHeaders)
        { return SendMessage(HttpMethod.Get, relativeUrl, null, useHttps, additionalHeaders); }

        protected HttpResponseMessage Post(string relativeUrl, object content, bool useHttps = false, params Tuple<string, string>[] additionalHeaders)
        { return SendMessage(HttpMethod.Post, relativeUrl, content, useHttps, additionalHeaders); }

        protected HttpResponseMessage Patch(string relativeUrl, object content, bool useHttps = false, params Tuple<string, string>[] additionalHeaders)
        { return SendMessage(new HttpMethod("PATCH"), relativeUrl, content, useHttps, additionalHeaders); }

        protected HttpResponseMessage Put(string relativeUrl, object content, bool useHttps = false, params Tuple<string, string>[] additionalHeaders)
        { return SendMessage(HttpMethod.Put, relativeUrl, content, useHttps, additionalHeaders); }

        protected HttpResponseMessage Delete(string relativeUrl, bool useHttps = false, params Tuple<string, string>[] additionalHeaders)
        { return SendMessage(HttpMethod.Delete, relativeUrl, null, useHttps, additionalHeaders); }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ApiControllerTestBase()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                if (_setup.SessionTeardown != null)
                    _setup.SessionTeardown(Session);
                _httpServer.Value.Dispose();
            }

            _disposed = true;
        }

        private HttpResponseMessage SendMessage(HttpMethod method, string relativeUrl, object content = null, bool useHttps = false,
            params Tuple<string, string>[] additionalHeaders)
        {
            var request = new HttpRequestMessage(method, new Uri(useHttps ? new Uri("https://localhost") : new Uri("http://localhost"), relativeUrl));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaType));
            if (additionalHeaders.Any())
                foreach (var header in additionalHeaders)
                    request.Headers.Add(header.Item1, header.Item2.Split(','));
            if (content != null)
                request.Content = new StringContent(JsonConvert.SerializeObject(content), Encoding.UTF8, MediaType);

            var response = new HttpClient(_httpServer.Value).SendAsync(request).Result;

            // for debugging
            if (response.StatusCode == HttpStatusCode.InternalServerError)
                Console.WriteLine("\r\nresponse.StatusCode == 500\r\nDetails:\r\n{0}\r\n",
                    response.Content.ReadAsStringAsync().Result);

            if (_setup.PostControllerAction != null)
                _setup.PostControllerAction(Session);

            return response;
        }
    }

    public static class ApiControllerTestExtentions
    {
        public static T BodyAs<T>(this HttpResponseMessage response)
        {
            if (typeof(T) == typeof(string))
                return (T)(object)response.Content.ReadAsStringAsync().Result;
            return response.Content.ReadAsAsync<T>().Result;
        }
    }

    public class ApiTestSetup<TSession> : ApiTestSetup
    {
        public ApiTestSetup(IContainer container,
            Action<HttpConfiguration> registerWebApiConfig,
            Action<ContainerBuilder> additionalConfig = null,
            Action<TSession> sessionSetup = null,
            Action<TSession> sessionTeardown = null,
            Action<TSession> postControllerAction = null)
            : base(container, registerWebApiConfig, additionalConfig)
        {
            SessionSetup = sessionSetup;
            SessionTeardown = sessionTeardown;
            PostControllerAction = postControllerAction;
        }

        public ApiTestSetup(TestSetup<TSession> setup, Action<HttpConfiguration> registerWebApiConfig)
            : base(setup.Container, registerWebApiConfig, setup.AdditionalConfig)
        {
            SessionSetup = setup.SessionSetup;
            SessionTeardown = setup.SessionTeardown;
            PostControllerAction = setup.AfterActAction;
        }

        internal ApiTestSetup(ApiTestSetup setup)
            : base(setup.Container, setup.RegisterWebApiConfig, setup.AdditionalConfig)
        { }

        internal Action<TSession> SessionSetup { get; private set; }
        internal Action<TSession> SessionTeardown { get; private set; }
        internal Action<TSession> PostControllerAction { get; private set; }
    }

    public class ApiTestSetup
    {
        public ApiTestSetup(IContainer container,
            Action<HttpConfiguration> registerWebApiConfig,
            Action<ContainerBuilder> additionalConfig = null)
        {
            if (container == null)
                throw new ArgumentNullException("container", "A real container must be supplied to setup tests");
            Container = container;

            if (registerWebApiConfig == null)
                throw new ArgumentNullException("registerWebApiConfig", "WebApi needs to configure itself before running tests.");
            RegisterWebApiConfig = registerWebApiConfig;

            AdditionalConfig = additionalConfig ?? (builder => { });
        }

        internal IContainer Container { get; private set; }
        internal Action<HttpConfiguration> RegisterWebApiConfig { get; private set; }
        internal Action<ContainerBuilder> AdditionalConfig { get; set; }
    }
}
