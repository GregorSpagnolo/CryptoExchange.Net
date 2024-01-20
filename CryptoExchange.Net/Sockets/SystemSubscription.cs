﻿using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CryptoExchange.Net.Sockets
{
    /// <summary>
    /// A system subscription
    /// </summary>
    public abstract class SystemSubscription : Subscription
    {
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="authenticated"></param>
        public SystemSubscription(ILogger logger, bool authenticated = false) : base(logger, authenticated, false)
        {
        }

        /// <inheritdoc />
        public override Query? GetSubQuery(SocketConnection connection) => null;

        /// <inheritdoc />
        public override Query? GetUnsubQuery() => null;
    }

    /// <inheritdoc />
    public abstract class SystemSubscription<T> : SystemSubscription
    {
        /// <inheritdoc />
        public override Type GetMessageType(SocketMessage message) => typeof(T);

        /// <inheritdoc />
        public override Task<CallResult> DoHandleMessageAsync(SocketConnection connection, DataEvent<object> message)
            => HandleMessageAsync(connection, message.As((T)message.Data));

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="logger"></param>
        /// <param name="authenticated"></param>
        protected SystemSubscription(ILogger logger, bool authenticated) : base(logger, authenticated)
        {
        }

        /// <summary>
        /// Handle an update message
        /// </summary>
        /// <param name="connection"></param>
        /// <param name="message"></param>
        /// <returns></returns>
        public abstract Task<CallResult> HandleMessageAsync(SocketConnection connection, DataEvent<T> message);
    }
}
