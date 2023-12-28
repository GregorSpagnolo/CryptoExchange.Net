﻿using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Objects.Sockets;
using CryptoExchange.Net.Sockets;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace CryptoExchange.Net.Interfaces
{
    public interface IMessageProcessor
    {
        public int Id { get; }
        public List<string> StreamIdentifiers { get; }
        Task<CallResult> HandleMessageAsync(SocketConnection connection, DataEvent<BaseParsedMessage> message);
        Dictionary<string, Type> TypeMapping { get; }
    }
}
