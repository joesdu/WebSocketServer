﻿using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;

namespace Cyaim.WebSocketServer
{
    /// <summary>
    /// IWebSocketSession
    /// </summary>
    public interface IWebSocketSession
    {
        /// <summary>
        /// Current request http context
        /// </summary>
        public HttpContext WebSocketHttpContext { get; set; }

        /// <summary>
        /// Current session web socket client
        /// </summary>
        public WebSocket WebSocketClient { get; set; }
    }
}
