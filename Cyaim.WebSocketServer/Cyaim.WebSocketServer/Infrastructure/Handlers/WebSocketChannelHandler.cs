﻿using Cyaim.WebSocketServer.Infrastructure.Attributes;
using Cyaim.WebSocketServer.Infrastructure.Configures;
using Cyaim.WebSocketServer.Middlewares;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;

namespace Cyaim.WebSocketServer.Infrastructure.Handlers
{
    public class WebSocketChannelHandler
    {

        public static ConcurrentDictionary<string, WebSocket> Clients { get; set; } = new ConcurrentDictionary<string, WebSocket>();

        public HttpContext context;
        public ILogger<WebSocketRouteMiddleware> logger;
        public WebSocketRouteOption webSocketOption;
        public WebSocket webSocket;

        public async Task MvcChannelHandler(HttpContext context, WebSocketManager webSocketManager, ILogger<WebSocketRouteMiddleware> logger, WebSocketRouteOption webSocketOptions)
        {
            this.context = context;
            this.logger = logger;
            this.webSocketOption = webSocketOptions;

            try
            {
                if (webSocketManager.IsWebSocketRequest)
                {
                    using (WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync())
                    {
                        this.webSocket = webSocket;

                        logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 连接已建立({context.Connection.Id})");
                        bool succ = Clients.TryAdd(context.Connection.Id, webSocket);
                        if (succ)
                        {
                            await Forward(context, webSocket);
                        }
                        else
                        {
                            throw new InvalidOperationException("客户端登录失败");
                        }


                    }
                }
                else
                {
                    logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 拒绝连接，缺少请求头({context.Connection.Id})");
                    context.Response.StatusCode = 400;
                }
            }
            catch (Exception)
            {
                throw;
            }
            finally
            {
                OnDisConnected(context, logger);
            }

        }


        /// <summary>
        /// 根据WebSocket数据类型转发
        /// </summary>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <returns></returns>
        private async Task Forward(HttpContext context, WebSocket webSocket)
        {
            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            switch (result.MessageType)
            {
                case WebSocketMessageType.Binary:
                    await BinaryForward(context, webSocket, result, buffer);
                    break;
                case WebSocketMessageType.Text:
                    await TextForward(result, buffer);
                    break;
            }

            //链接断开
            await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }

        /// <summary>
        /// 文本请求转发
        /// </summary>
        /// <param name="result"></param>
        /// <param name="buffer"></param>
        /// <returns></returns>
        private async Task TextForward(WebSocketReceiveResult result, byte[] buffer)
        {

            long requestTime = DateTime.Now.Ticks;
            StringBuilder json = new StringBuilder();
            //string json = string.Empty;

            //处理第一次返回的数据
            json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));
            //json += Encoding.UTF8.GetString(buffer[..result.Count]);

            //第一次接受已经接受完数据了
            if (result.EndOfMessage)
            {
                try
                {

                    MvcRequestScheme request = JsonConvert.DeserializeObject<MvcRequestScheme>(json.ToString());
                    //按请求节点转发
                    object invokeResult = await DistributeAsync(webSocketOption, context, webSocket, request, logger);
                    string serialJson = JsonConvert.SerializeObject(invokeResult ?? string.Empty);

                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);
                    //json = string.Empty;
                    json = json.Clear();
                }
                catch (JsonSerializationException ex)
                {
                    MvcResponseScheme mvcResponse = new MvcResponseScheme()
                    {
                        Status = 1,
                        ReauestTime = requestTime,
                        ComplateTime = DateTime.Now.Ticks,
                        Msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> \r\n {ex.Message}\r\n{ex.StackTrace}",
                    };

                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
                }


            }

            //等待客户端发送数据，第二次接受数据
            while (!result.CloseStatus.HasValue)
            {
                try
                {
                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);


                    json = json.Append(Encoding.UTF8.GetString(buffer[..result.Count]));
                    //json += Encoding.UTF8.GetString(buffer[..result.Count]);

                    if (!result.EndOfMessage || result.CloseStatus.HasValue)
                    {
                        continue;
                    }

                    MvcRequestScheme request = JsonConvert.DeserializeObject<MvcRequestScheme>(json.ToString());

                    //按节点请求转发
                    object invokeResult = await DistributeAsync(webSocketOption, context, webSocket, request, logger);

                    string serialJson = JsonConvert.SerializeObject(invokeResult ?? string.Empty);

                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(serialJson)), result.MessageType, result.EndOfMessage, CancellationToken.None);

                }
                catch (JsonSerializationException ex)
                {
                    MvcResponseScheme mvcResponse = new MvcResponseScheme()
                    {
                        Status = 1,
                        ReauestTime = requestTime,
                        ComplateTime = DateTime.Now.Ticks,
                        Msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> \r\n {ex.Message}\r\n{ex.StackTrace}",
                    };
                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
                }
                catch (JsonReaderException ex)
                {
                    MvcResponseScheme mvcResponse = new MvcResponseScheme()
                    {
                        Status = 1,
                        ReauestTime = requestTime,
                        ComplateTime = DateTime.Now.Ticks,
                        Msg = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 请求解析错误\r\n {ex.Message}\r\n{ex.StackTrace}",
                    };
                    await webSocket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(mvcResponse))), result.MessageType, result.EndOfMessage, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, ex.Message);
                }
                finally
                {
                    //json = string.Empty;
                    json = json.Clear();
                }

            }


        }

        private async Task BinaryForward(HttpContext context, WebSocket webSocket, WebSocketReceiveResult result, byte[] buffer)
        {

        }

        /// <summary>
        /// 使用并发安全的字段类型缓存请求Path与处理请求函数
        /// </summary>
        private static ConcurrentDictionary<string, MethodInfo> ExecuteMethods = new ConcurrentDictionary<string, MethodInfo>();

        /// <summary>
        /// 转发请求
        /// </summary>
        /// <param name="webSocketOptions"></param>
        /// <param name="context"></param>
        /// <param name="webSocket"></param>
        /// <param name="request"></param>
        /// <returns></returns>
        public static async Task<object> DistributeAsync(WebSocketRouteOption webSocketOptions, HttpContext context, WebSocket webSocket, MvcRequestScheme request, ILogger<WebSocketRouteMiddleware> logger)
        {
            long requestTime = DateTime.Now.Ticks;
            string requestPath = request.Target;
            JObject requestBody = request.Body as JObject;

            try
            {
                // 从键值对中获取对应的执行函数 
                webSocketOptions.WatchAssemblyContext.WatchMethods.TryGetValue(requestPath, out MethodInfo method);

                if (method != null)
                {
                    Type clss = webSocketOptions.WatchAssemblyContext.WatchEndPoint.FirstOrDefault(x => x.MethodPath == requestPath)?.Class;
                    if (clss == null)
                    {
                        //找不到访问目标

                        return null;
                    }

                    #region 注入Socket的HttpContext和WebSocket客户端
                    PropertyInfo contextInfo = clss.GetProperty(webSocketOptions.InjectionHttpContextPropertyName);
                    PropertyInfo socketInfo = clss.GetProperty(webSocketOptions.InjectionWebSocketClientPropertyName);

                    webSocketOptions.WatchAssemblyContext.MaxCoustructorParameters.TryGetValue(clss, out ConstructorParameter constructorParameter);

                    object[] instanceParmas = new object[constructorParameter.ParameterInfos.Length];

                    for (int i = 0; i < constructorParameter.ParameterInfos.Length; i++)
                    {
                        ParameterInfo item = constructorParameter.ParameterInfos[i];

                        instanceParmas[i] = WebSocketRouteOption.ApplicationServices.GetService(item.ParameterType);
                    }

                    object inst = Activator.CreateInstance(clss, instanceParmas);

                    if (contextInfo != null && contextInfo.CanWrite)
                    {
                        contextInfo.SetValue(inst, context);
                    }
                    if (socketInfo != null && socketInfo.CanWrite)
                    {
                        socketInfo.SetValue(inst, webSocket);
                    }
                    #endregion

                    #region 注入调用方法参数
                    Stopwatch stopwatch1 = new Stopwatch();
                    stopwatch1.Start();
                    MvcResponseScheme mvcResponse = new MvcResponseScheme() { Status = 0 };
                    object invokeResult = default;
                    if (requestBody == null)
                    {
                        //无参方法
                        invokeResult = method.Invoke(inst, new object[0]);
                    }
                    else
                    {
                        // 异步调用该方法 
                        webSocketOptions.WatchAssemblyContext.MethodParameters.TryGetValue(method, out ParameterInfo[] methodParam);

                        Task<object> invoke = new Task<object>(() =>
                        {
                            object[] methodParm = new object[methodParam.Length];
                            for (int i = 0; i < methodParam.Length; i++)
                            {
                                ParameterInfo item = methodParam[i];
                                Type methodParmType = item.ParameterType;

                                //检测方法中的参数是否是C#定义类型
                                bool isBaseType = methodParmType.IsBasicType();
                                object parmVal = null;
                                try
                                {
                                    if (isBaseType)
                                    {
                                        //C#定义数据类型，按参数名取json value
                                        bool hasVal = requestBody.TryGetValue(item.Name, out JToken jToken);
                                        if (hasVal)
                                        {
                                            parmVal = jToken.ToObject(methodParmType);
                                        }
                                        else
                                        {
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        //自定义类，反序列化
                                        var classParmVal = JsonConvert.DeserializeObject(requestBody.ToString(), methodParmType);

                                        parmVal = classParmVal;
                                    }
                                }
                                catch (JsonReaderException ex)
                                {
                                    //反序列化失败
                                    //parmVal = null;
                                    logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求反序列异常\r\n{ex.Message}\r\n{ex.StackTrace}");
                                }
                                catch (FormatException ex)
                                {
                                    //jToken.ToObject 抛出 类型转换失败
                                    logger.LogError($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> {requestPath} 请求的方法参数数据格式化异常\r\n{ex.Message}\r\n{ex.StackTrace}");
                                }
                                methodParm[i] = parmVal;
                            }

                            return method.Invoke(inst, methodParm);
                        });
                        invoke.Start();

                        invokeResult = await invoke;
                    }
                    #endregion

                    mvcResponse.Body = invokeResult;
                    mvcResponse.ComplateTime = DateTime.Now.Ticks;
                    return mvcResponse;
                }
            }
            catch (Exception ex)
            {
                return new MvcResponseScheme() { Status = 1, Msg = $@"Target:{requestPath}\r\n{ex.Message}\r\n{ex.StackTrace}", ReauestTime = requestTime, ComplateTime = DateTime.Now.Ticks };
            }


            return null;
        }

        /// <summary>
        /// 客户端断开链接
        /// </summary>
        /// <param name="context"></param>
        /// <param name="logger"></param>
        /// <returns></returns>
        private void OnDisConnected(HttpContext context, ILogger<WebSocketRouteMiddleware> logger)
        {
            logger.LogWarning($"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort} -> 连接已断开({context.Connection.Id})");

            bool wsExists = Clients.ContainsKey(context.Connection.Id);
            if (wsExists)
            {
                Clients.TryRemove(context.Connection.Id, out WebSocket ws);
            }

        }


    }
}