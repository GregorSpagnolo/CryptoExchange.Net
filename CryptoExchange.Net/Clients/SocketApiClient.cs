using CryptoExchange.Net.Converters;
using CryptoExchange.Net.Interfaces;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Options;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryptoExchange.Net
{
    /// <summary>
    /// Base socket API client for interaction with a websocket API
    /// </summary>
    public abstract class SocketApiClient : BaseApiClient, ISocketApiClient
    {
        #region Fields
        /// <inheritdoc/>
        public IWebsocketFactory SocketFactory { get; set; } = new WebsocketFactory();

        /// <summary>
        /// List of socket connections currently connecting/connected
        /// </summary>
        protected internal ConcurrentDictionary<int, SocketConnection> socketConnections = new();

        /// <summary>
        /// Semaphore used while creating sockets
        /// </summary>
        protected internal readonly SemaphoreSlim semaphoreSlim = new(1);

        /// <summary>
        /// Keep alive interval for websocket connection
        /// </summary>
        protected TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Delegate used for manipulating data received from socket connections before it is processed by listeners
        /// </summary>
        protected Func<Stream, Stream>? interceptor;

        /// <summary>
        /// Handlers for data from the socket which doesn't need to be forwarded to the caller. Ping or welcome messages for example.
        /// </summary>
        protected List<SystemSubscription> systemSubscriptions = new();

        /// <summary>
        /// The task that is sending periodic data on the websocket. Can be used for sending Ping messages every x seconds or similair. Not necesarry.
        /// </summary>
        protected Task? periodicTask;

        /// <summary>
        /// Wait event for the periodicTask
        /// </summary>
        protected AsyncResetEvent? periodicEvent;

        /// <summary>
        /// If true; data which is a response to a query will also be distributed to subscriptions
        /// If false; data which is a response to a query won't get forwarded to subscriptions as well
        /// </summary>
        protected internal bool ContinueOnQueryResponse { get; protected set; }

        /// <summary>
        /// If a message is received on the socket which is not handled by a handler this boolean determines whether this logs an error message
        /// </summary>
        protected internal bool UnhandledMessageExpected { get; set; }

        /// <summary>
        /// The rate limiters 
        /// </summary>
        protected internal IEnumerable<IRateLimiter>? RateLimiters { get; set; }

        /// <inheritdoc />
        public double IncomingKbps
        {
            get
            {
                if (!socketConnections.Any())
                    return 0;

                return socketConnections.Sum(s => s.Value.IncomingKbps);
            }
        }

        /// <inheritdoc />
        public int CurrentConnections => socketConnections.Count;

        /// <inheritdoc />
        public int CurrentSubscriptions
        {
            get
            {
                if (!socketConnections.Any())
                    return 0;

                return socketConnections.Sum(s => s.Value.UserSubscriptionCount);
            }
        }


        /// <inheritdoc />
        public new SocketExchangeOptions ClientOptions => (SocketExchangeOptions)base.ClientOptions;

        /// <inheritdoc />
        public new SocketApiOptions ApiOptions => (SocketApiOptions)base.ApiOptions;

        /// <inheritdoc />
        public abstract MessageInterpreterPipeline Pipeline { get; }
        #endregion

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger">log</param>
        /// <param name="options">Client options</param>
        /// <param name="baseAddress">Base address for this API client</param>
        /// <param name="apiOptions">The Api client options</param>
        public SocketApiClient(ILogger logger, string baseAddress, SocketExchangeOptions options, SocketApiOptions apiOptions) 
            : base(logger, 
                  apiOptions.OutputOriginalData ?? options.OutputOriginalData,
                  apiOptions.ApiCredentials ?? options.ApiCredentials,
                  baseAddress,
                  options,
                  apiOptions)
        {
            var rateLimiters = new List<IRateLimiter>();
            foreach (var rateLimiter in apiOptions.RateLimiters)
                rateLimiters.Add(rateLimiter);
            RateLimiters = rateLimiters;
        }

        /// <summary>
        /// Set a delegate which can manipulate the message stream before it is processed by listeners
        /// </summary>
        /// <param name="interceptor">Interceptor</param>
        protected void SetInterceptor(Func<Stream, Stream> interceptor)
        {
            this.interceptor = interceptor;
        }

        /// <summary>
        /// Connect to an url and listen for data on the BaseAddress
        /// </summary>
        /// <typeparam name="T">The type of the expected data</typeparam>
        /// <param name="subscription">The subscription</param>
        /// <param name="ct">Cancellation token for closing this subscription</param>
        /// <returns></returns>
        protected virtual Task<CallResult<UpdateSubscription>> SubscribeAsync(Subscription subscription, CancellationToken ct)
        {
            return SubscribeAsync(BaseAddress, subscription, ct);
        }

        /// <summary>
        /// Connect to an url and listen for data
        /// </summary>
        /// <typeparam name="T">The type of the expected data</typeparam>
        /// <param name="url">The URL to connect to</param>
        /// <param name="subscription">The subscription</param>
        /// <param name="ct">Cancellation token for closing this subscription</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<UpdateSubscription>> SubscribeAsync(string url, Subscription subscription, CancellationToken ct)
        {
            if (_disposing)
                return new CallResult<UpdateSubscription>(new InvalidOperationError("Client disposed, can't subscribe"));

            if (subscription.Authenticated && AuthenticationProvider == null)
                return new CallResult<UpdateSubscription>(new NoApiCredentialsError());

            SocketConnection socketConnection;
            var released = false;
            // Wait for a semaphore here, so we only connect 1 socket at a time.
            // This is necessary for being able to see if connections can be combined
            try
            {
                await semaphoreSlim.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new CallResult<UpdateSubscription>(new CancellationRequestedError());
            }

            try
            {
                while (true)
                {
                    // Get a new or existing socket connection
                    var socketResult = await GetSocketConnection(url, subscription.Authenticated).ConfigureAwait(false);
                    if (!socketResult)
                        return socketResult.As<UpdateSubscription>(null);

                    socketConnection = socketResult.Data;

                    // Add a subscription on the socket connection
                    var success = socketConnection.CanAddSubscription();
                    if (!success)
                    {
                        _logger.Log(LogLevel.Trace, $"Socket {socketConnection.SocketId} failed to add subscription, retrying on different connection");
                        continue;
                    }

                    if (ClientOptions.SocketSubscriptionsCombineTarget == 1)
                    {
                        // Only 1 subscription per connection, so no need to wait for connection since a new subscription will create a new connection anyway
                        semaphoreSlim.Release();
                        released = true;
                    }

                    var needsConnecting = !socketConnection.Connected;

                    var connectResult = await ConnectIfNeededAsync(socketConnection, subscription.Authenticated).ConfigureAwait(false);
                    if (!connectResult)
                        return new CallResult<UpdateSubscription>(connectResult.Error!);

                    break;
                }
            }
            finally
            {
                if (!released)
                    semaphoreSlim.Release();
            }

            if (socketConnection.PausedActivity)
            {
                _logger.Log(LogLevel.Warning, $"Socket {socketConnection.SocketId} has been paused, can't subscribe at this moment");
                return new CallResult<UpdateSubscription>(new ServerError("Socket is paused"));
            }

            var subQuery = subscription.GetSubQuery(socketConnection);
            if (subQuery != null)
            {
                // Send the request and wait for answer
                var subResult = await socketConnection.SendAndWaitQueryAsync(subQuery).ConfigureAwait(false);
                if (!subResult)
                {
                    _logger.Log(LogLevel.Warning, $"Socket {socketConnection.SocketId} failed to subscribe: {subResult.Error}");
                    // If this was a timeout we still need to send an unsubscribe to prevent messages coming in later
                    var unsubscribe = subResult.Error is CancellationRequestedError;
                    await socketConnection.CloseAsync(subscription, unsubscribe).ConfigureAwait(false);

                    return new CallResult<UpdateSubscription>(subResult.Error!);
                }

                subscription.HandleSubQueryResponse(subQuery.Response);
            }

            subscription.Confirmed = true;
            if (ct != default)
            {
                subscription.CancellationTokenRegistration = ct.Register(async () =>
                {
                    _logger.Log(LogLevel.Information, $"Socket {socketConnection.SocketId} Cancellation token set, closing subscription {subscription.Id}");
                    await socketConnection.CloseAsync(subscription).ConfigureAwait(false);
                }, false);
            }

            socketConnection.AddSubscription(subscription);
            _logger.Log(LogLevel.Information, $"Socket {socketConnection.SocketId} subscription {subscription.Id} completed successfully");
            return new CallResult<UpdateSubscription>(new UpdateSubscription(socketConnection, subscription));
        }

        /// <summary>
        /// Send a query on a socket connection to the BaseAddress and wait for the response
        /// </summary>
        /// <typeparam name="T">Expected result type</typeparam>
        /// <param name="query">The query</param>
        /// <returns></returns>
        protected virtual Task<CallResult<T>> QueryAsync<T>(Query<T> query)
        {
            return QueryAsync(BaseAddress, query);
        }

        /// <summary>
        /// Send a query on a socket connection and wait for the response
        /// </summary>
        /// <typeparam name="T">The expected result type</typeparam>
        /// <param name="url">The url for the request</param>
        /// <param name="query">The query</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<T>> QueryAsync<T>(string url, Query<T> query)
        {
            if (_disposing)
                return new CallResult<T>(new InvalidOperationError("Client disposed, can't query"));

            SocketConnection socketConnection;
            var released = false;
            await semaphoreSlim.WaitAsync().ConfigureAwait(false);
            try
            {
                var socketResult = await GetSocketConnection(url, query.Authenticated).ConfigureAwait(false);
                if (!socketResult)
                    return socketResult.As<T>(default);

                socketConnection = socketResult.Data;

                if (ClientOptions.SocketSubscriptionsCombineTarget == 1)
                {
                    // Can release early when only a single sub per connection
                    semaphoreSlim.Release();
                    released = true;
                }

                var connectResult = await ConnectIfNeededAsync(socketConnection, query.Authenticated).ConfigureAwait(false);
                if (!connectResult)
                    return new CallResult<T>(connectResult.Error!);
            }
            finally
            {
                if (!released)
                    semaphoreSlim.Release();
            }

            if (socketConnection.PausedActivity)
            {
                _logger.Log(LogLevel.Warning, $"Socket {socketConnection.SocketId} has been paused, can't send query at this moment");
                return new CallResult<T>(new ServerError("Socket is paused"));
            }

            return await socketConnection.SendAndWaitQueryAsync(query).ConfigureAwait(false);
        }

        /// <summary>
        /// Checks if a socket needs to be connected and does so if needed. Also authenticates on the socket if needed
        /// </summary>
        /// <param name="socket">The connection to check</param>
        /// <param name="authenticated">Whether the socket should authenticated</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<bool>> ConnectIfNeededAsync(SocketConnection socket, bool authenticated)
        {
            if (socket.Connected)
                return new CallResult<bool>(true);

            var connectResult = await ConnectSocketAsync(socket).ConfigureAwait(false);
            if (!connectResult)
                return new CallResult<bool>(connectResult.Error!);

            if (ClientOptions.DelayAfterConnect != TimeSpan.Zero)
                await Task.Delay(ClientOptions.DelayAfterConnect).ConfigureAwait(false);

            if (!authenticated || socket.Authenticated)
                return new CallResult<bool>(true);

            return await AuthenticateSocketAsync(socket).ConfigureAwait(false);
        }

        /// <summary>
        /// Authenticate a socket connection
        /// </summary>
        /// <param name="socket">Socket to authenticate</param>
        /// <returns></returns>
        public virtual async Task<CallResult<bool>> AuthenticateSocketAsync(SocketConnection socket)
        {
            if (AuthenticationProvider == null)
                return new CallResult<bool>(new NoApiCredentialsError());

            _logger.Log(LogLevel.Debug, $"Socket {socket.SocketId} Attempting to authenticate");
            var authRequest = GetAuthenticationRequest();
            var result = await socket.SendAndWaitQueryAsync(authRequest).ConfigureAwait(false);

            if (!result)
            {
                _logger.Log(LogLevel.Warning, $"Socket {socket.SocketId} authentication failed");
                if (socket.Connected)
                    await socket.CloseAsync().ConfigureAwait(false);

                result.Error!.Message = "Authentication failed: " + result.Error.Message;
                return new CallResult<bool>(result.Error)!;
            }

            _logger.Log(LogLevel.Debug, $"Socket {socket.SocketId} authenticated");
            socket.Authenticated = true;
            return new CallResult<bool>(true);
        }

        /// <summary>
        /// Should return the request which can be used to authenticate a socket connection
        /// </summary>
        /// <returns></returns>
        protected internal virtual BaseQuery GetAuthenticationRequest() => throw new NotImplementedException();

        /// <summary>
        /// Adds a system subscription. Used for example to reply to ping requests
        /// </summary>
        /// <param name="systemSubscription">The subscription</param>
        protected void AddSystemSubscription(SystemSubscription systemSubscription)
        {
            systemSubscriptions.Add(systemSubscription);
            foreach (var connection in socketConnections.Values)
                connection.AddSubscription(systemSubscription);
        }

        /// <summary>
        /// Get the url to connect to (defaults to BaseAddress form the client options)
        /// </summary>
        /// <param name="address"></param>
        /// <param name="authentication"></param>
        /// <returns></returns>
        protected virtual Task<CallResult<string?>> GetConnectionUrlAsync(string address, bool authentication)
        {
            return Task.FromResult(new CallResult<string?>(address));
        }

        /// <summary>
        /// Get the url to reconnect to after losing a connection
        /// </summary>
        /// <param name="connection"></param>
        /// <returns></returns>
        protected internal virtual Task<Uri?> GetReconnectUriAsync(SocketConnection connection)
        {
            return Task.FromResult<Uri?>(connection.ConnectionUri);
        }

        /// <summary>
        /// Update the original request to send when the connection is restored after disconnecting. Can be used to update an authentication token for example.
        /// </summary>
        /// <param name="request">The original request</param>
        /// <returns></returns>
        protected internal virtual Task<CallResult<object>> RevitalizeRequestAsync(object request)
        {
            return Task.FromResult(new CallResult<object>(request));
        }

        /// <summary>
        /// Gets a connection for a new subscription or query. Can be an existing if there are open position or a new one.
        /// </summary>
        /// <param name="address">The address the socket is for</param>
        /// <param name="authenticated">Whether the socket should be authenticated</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<SocketConnection>> GetSocketConnection(string address, bool authenticated)
        {
            var socketResult = socketConnections.Where(s => (s.Value.Status == SocketConnection.SocketStatus.None || s.Value.Status == SocketConnection.SocketStatus.Connected)
                                                  && s.Value.Tag.TrimEnd('/') == address.TrimEnd('/')
                                                  && (s.Value.ApiClient.GetType() == GetType())
                                                  && (s.Value.Authenticated == authenticated || !authenticated) && s.Value.Connected).OrderBy(s => s.Value.UserSubscriptionCount).FirstOrDefault();
            var result = socketResult.Equals(default(KeyValuePair<int, SocketConnection>)) ? null : socketResult.Value;
            if (result != null)
            {
                if (result.UserSubscriptionCount < ClientOptions.SocketSubscriptionsCombineTarget || (socketConnections.Count >= (ApiOptions.MaxSocketConnections ?? ClientOptions.MaxSocketConnections) && socketConnections.All(s => s.Value.UserSubscriptionCount >= ClientOptions.SocketSubscriptionsCombineTarget)))
                {
                    // Use existing socket if it has less than target connections OR it has the least connections and we can't make new
                    return new CallResult<SocketConnection>(result);
                }
            }

            var connectionAddress = await GetConnectionUrlAsync(address, authenticated).ConfigureAwait(false);
            if (!connectionAddress)
            {
                _logger.Log(LogLevel.Warning, $"Failed to determine connection url: " + connectionAddress.Error);
                return connectionAddress.As<SocketConnection>(null);
            }

            if (connectionAddress.Data != address)
                _logger.Log(LogLevel.Debug, $"Connection address set to " + connectionAddress.Data);

            // Create new socket
            var socket = CreateSocket(connectionAddress.Data!);
            var socketConnection = new SocketConnection(_logger, this, socket, address);
            socketConnection.UnhandledMessage += HandleUnhandledMessage;
            socketConnection.UnparsedMessage += HandleUnparsedMessage;

            foreach (var systemSubscription in systemSubscriptions)
                socketConnection.AddSubscription(systemSubscription);

            return new CallResult<SocketConnection>(socketConnection);
        }

        /// <summary>
        /// Process an unhandled message
        /// </summary>
        /// <param name="message">The message that wasn't processed</param>
        protected virtual void HandleUnhandledMessage(BaseParsedMessage message)
        {
        }

        /// <summary>
        /// Process an unparsed message
        /// </summary>
        /// <param name="message">The message that wasn't parsed</param>
        protected virtual void HandleUnparsedMessage(byte[] message)
        {
        }

        /// <summary>
        /// Connect a socket
        /// </summary>
        /// <param name="socketConnection">The socket to connect</param>
        /// <returns></returns>
        protected virtual async Task<CallResult<bool>> ConnectSocketAsync(SocketConnection socketConnection)
        {
            if (await socketConnection.ConnectAsync().ConfigureAwait(false))
            {
                socketConnections.TryAdd(socketConnection.SocketId, socketConnection);
                return new CallResult<bool>(true);
            }

            socketConnection.Dispose();
            return new CallResult<bool>(new CantConnectError());
        }

        /// <summary>
        /// Get parameters for the websocket connection
        /// </summary>
        /// <param name="address">The address to connect to</param>
        /// <returns></returns>
        protected virtual WebSocketParameters GetWebSocketParameters(string address)
            => new(new Uri(address), ClientOptions.AutoReconnect)
            {
                Interceptor = interceptor,
                KeepAliveInterval = KeepAliveInterval,
                ReconnectInterval = ClientOptions.ReconnectInterval,
                RateLimiters = RateLimiters,
                Proxy = ClientOptions.Proxy,
                Timeout = ApiOptions.SocketNoDataTimeout ?? ClientOptions.SocketNoDataTimeout
            };

        /// <summary>
        /// Create a socket for an address
        /// </summary>
        /// <param name="address">The address the socket should connect to</param>
        /// <returns></returns>
        protected virtual IWebsocket CreateSocket(string address)
        {
            var socket = SocketFactory.CreateWebsocket(_logger, GetWebSocketParameters(address));
            _logger.Log(LogLevel.Debug, $"Socket {socket.Id} new socket created for " + address);
            return socket;
        }

        /// <summary>
        /// Periodically sends data over a socket connection
        /// </summary>
        /// <param name="identifier">Identifier for the periodic send</param>
        /// <param name="interval">How often</param>
        /// <param name="queryDelegate">Method returning the query to send</param>
        /// <param name="callback">The callback for processing the response</param>
        protected virtual void QueryPeriodic(string identifier, TimeSpan interval, Func<SocketConnection, BaseQuery> queryDelegate, Action<CallResult>? callback)
        {
            if (queryDelegate == null)
                throw new ArgumentNullException(nameof(queryDelegate));

            // TODO instead of having this on ApiClient level, this should be registered on the socket connection
            // This would prevent this looping without any connections

            periodicEvent = new AsyncResetEvent();
            periodicTask = Task.Run(async () =>
            {
                while (!_disposing)
                {
                    await periodicEvent.WaitAsync(interval).ConfigureAwait(false);
                    if (_disposing)
                        break;

                    foreach (var socketConnection in socketConnections.Values)
                    {
                        if (_disposing)
                            break;

                        if (!socketConnection.Connected)
                            continue;

                        var query = queryDelegate(socketConnection);
                        if (query == null)
                            continue;

                        _logger.Log(LogLevel.Trace, $"Socket {socketConnection.SocketId} sending periodic {identifier}");

                        try
                        {
                            var result = await socketConnection.SendAndWaitQueryAsync(query).ConfigureAwait(false);
                            callback?.Invoke(result);
                        }
                        catch (Exception ex)
                        {
                            _logger.Log(LogLevel.Warning, $"Socket {socketConnection.SocketId} Periodic send {identifier} failed: " + ex.ToLogString());
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Unsubscribe an update subscription
        /// </summary>
        /// <param name="subscriptionId">The id of the subscription to unsubscribe</param>
        /// <returns></returns>
        public virtual async Task<bool> UnsubscribeAsync(int subscriptionId)
        {
            Subscription? subscription = null;
            SocketConnection? connection = null;
            foreach (var socket in socketConnections.Values.ToList())
            {
                subscription = socket.GetSubscription(subscriptionId);
                if (subscription != null)
                {
                    connection = socket;
                    break;
                }
            }

            if (subscription == null || connection == null)
                return false;

            _logger.Log(LogLevel.Information, $"Socket {connection.SocketId} Unsubscribing subscription " + subscriptionId);
            await connection.CloseAsync(subscription).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Unsubscribe an update subscription
        /// </summary>
        /// <param name="subscription">The subscription to unsubscribe</param>
        /// <returns></returns>
        public virtual async Task UnsubscribeAsync(UpdateSubscription subscription)
        {
            if (subscription == null)
                throw new ArgumentNullException(nameof(subscription));

            _logger.Log(LogLevel.Information, $"Socket {subscription.SocketId} Unsubscribing subscription  " + subscription.Id);
            await subscription.CloseAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Unsubscribe all subscriptions
        /// </summary>
        /// <returns></returns>
        public virtual async Task UnsubscribeAllAsync()
        {
            var sum = socketConnections.Sum(s => s.Value.UserSubscriptionCount);
            if (sum == 0)
                return;

            _logger.Log(LogLevel.Information, $"Unsubscribing all {socketConnections.Sum(s => s.Value.UserSubscriptionCount)} subscriptions");
            var tasks = new List<Task>();
            {
                var socketList = socketConnections.Values;
                foreach (var sub in socketList)
                    tasks.Add(sub.CloseAsync()); 
            }

            await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
        }

        /// <summary>
        /// Reconnect all connections
        /// </summary>
        /// <returns></returns>
        public virtual async Task ReconnectAsync()
        {
            _logger.Log(LogLevel.Information, $"Reconnecting all {socketConnections.Count} connections");
            var tasks = new List<Task>();
            {
                var socketList = socketConnections.Values;
                foreach (var sub in socketList)
                    tasks.Add(sub.TriggerReconnectAsync());
            }

            await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
        }

        /// <summary>
        /// Log the current state of connections and subscriptions
        /// </summary>
        public string GetSubscriptionsState()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{GetType().Name}");
            sb.AppendLine($"  Connections: {socketConnections.Count}");
            sb.AppendLine($"  Subscriptions: {CurrentSubscriptions}");
            sb.AppendLine($"  Download speed: {IncomingKbps} kbps");
            foreach (var connection in socketConnections)
            {
                sb.AppendLine($"    Id: {connection.Key}");
                sb.AppendLine($"    Address: {connection.Value.ConnectionUri}");
                sb.AppendLine($"    Subscriptions: {connection.Value.UserSubscriptionCount}");
                sb.AppendLine($"    Status: {connection.Value.Status}");
                sb.AppendLine($"    Authenticated: {connection.Value.Authenticated}");
                sb.AppendLine($"    Download speed: {connection.Value.IncomingKbps} kbps");
                sb.AppendLine($"    Subscriptions:");
                foreach (var subscription in connection.Value.Subscriptions)
                {
                    sb.AppendLine($"      Id: {subscription.Id}");
                    sb.AppendLine($"      Confirmed: {subscription.Confirmed}");
                    sb.AppendLine($"      Invocations: {subscription.TotalInvocations}");
                    sb.AppendLine($"      Identifiers: [{string.Join(", ", subscription.StreamIdentifiers)}]");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Dispose the client
        /// </summary>
        public override void Dispose()
        {
            _disposing = true;
            periodicEvent?.Set();
            periodicEvent?.Dispose();
            if (socketConnections.Sum(s => s.Value.UserSubscriptionCount) > 0)
            {
                _logger.Log(LogLevel.Debug, "Disposing socket client, closing all subscriptions");
                _ = UnsubscribeAllAsync();
            }
            semaphoreSlim?.Dispose();
            base.Dispose();
        }
    }
}
