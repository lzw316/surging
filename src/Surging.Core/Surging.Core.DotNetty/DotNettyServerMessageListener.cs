﻿using DotNetty.Buffers;
using DotNetty.Codecs;
using DotNetty.Common;
using DotNetty.Common.Concurrency;
using DotNetty.Common.Utilities;
using DotNetty.Handlers.Timeout;
using DotNetty.Transport.Bootstrapping;
using DotNetty.Transport.Channels;
using DotNetty.Transport.Channels.Sockets;
using DotNetty.Transport.Libuv;
using Microsoft.Extensions.Logging;
using Surging.Core.CPlatform;
using Surging.Core.CPlatform.Messages;
using Surging.Core.CPlatform.Transport;
using Surging.Core.CPlatform.Transport.Codec;
using Surging.Core.DotNetty.Adapter;
using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Surging.Core.DotNetty
{
    public class DotNettyServerMessageListener : IMessageListener, IDisposable
    {
        #region Field

        private readonly ILogger<DotNettyServerMessageListener> _logger;
        private readonly ITransportMessageDecoder _transportMessageDecoder;
        private readonly ITransportMessageEncoder _transportMessageEncoder;
        private IChannel _channel;

        #endregion Field

        #region Constructor

        public DotNettyServerMessageListener(ILogger<DotNettyServerMessageListener> logger, ITransportMessageCodecFactory codecFactory)
        {
            _logger = logger;
            _transportMessageEncoder = codecFactory.GetEncoder();
            _transportMessageDecoder = codecFactory.GetDecoder();
        }

        #endregion Constructor

        #region Implementation of IMessageListener

        public event ReceivedDelegate Received;

        /// <summary>
        /// 触发接收到消息事件。
        /// </summary>
        /// <param name="sender">消息发送者。</param>
        /// <param name="message">接收到的消息。</param>
        /// <returns>一个任务。</returns> 
        public async Task OnReceived(IMessageSender sender, TransportMessage message)
        {
            if (Received == null)
                return;
            await Received(sender, message);
        }

        #endregion Implementation of IMessageListener

        public async Task StartAsync(EndPoint endPoint)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug($"准备启动服务主机，监听地址：{endPoint}。");
            IEventLoopGroup bossGroup = new MultithreadEventLoopGroup(1);
            IEventLoopGroup workerGroup = new MultithreadEventLoopGroup();//Default eventLoopCount is Environment.ProcessorCount * 2
            var bootstrap = new ServerBootstrap();
            IEventLoopGroup eventExecutor = new MultithreadEventLoopGroup();
            if (AppConfig.ServerOptions.Libuv)
            {
                ResourceLeakDetector.Level = ResourceLeakDetector.DetectionLevel.Disabled;
                var dispatcher = new DispatcherEventLoopGroup();
                bossGroup = dispatcher;
                workerGroup = new WorkerEventLoopGroup(dispatcher, AppConfig.ServerOptions.EventLoopCount);
                var dispatcherExecutor = new DispatcherEventLoopGroup();
                eventExecutor = new WorkerEventLoopGroup(dispatcherExecutor, AppConfig.ServerOptions.EventLoopCount);
                bootstrap.Channel<TcpServerChannel>();
            }
            else
            {
                bossGroup = new MultithreadEventLoopGroup(1);
                workerGroup = new MultithreadEventLoopGroup(AppConfig.ServerOptions.EventLoopCount);
                bootstrap.Channel<TcpServerSocketChannel>();
            }
            bootstrap
            .Option(ChannelOption.SoBacklog, AppConfig.ServerOptions.SoBacklog)
            .ChildOption(ChannelOption.Allocator, UnpooledByteBufferAllocator.Default)
            .ChildOption(ChannelOption.TcpNodelay, true)
            .ChildOption(ChannelOption.SoReuseaddr, true)
            .Group(bossGroup, workerGroup)
            .ChildHandler(new ActionChannelInitializer<IChannel>(channel =>
            {
                var pipeline = channel.Pipeline;
                pipeline.AddLast(new LengthFieldPrepender2(4));
                /* best settings for multiplexing */
                // pipeline.AddLast(new LengthFieldBasedFrameDecoder2(int.MaxValue, 0, 4, 0, 4)); //Video stream push  
                //pipeline.AddLast(new LengthFieldBasedFrameDecoder2(1024*1024*50, 0, 4, 0, 4)); //big data  50m-200m
                // pipeline.AddLast(new LengthFieldBasedFrameDecoder2(1024*1024*4, 0, 4, 0, 4)); //small data 4m 
                pipeline.AddLast(new TransportMessageHandlerEncoder(_transportMessageEncoder));
                pipeline.AddLast(eventExecutor, "HandlerAdapter", new TransportMessageChannelHandlerAdapter(_transportMessageDecoder));
                pipeline.AddLast(eventExecutor, "ServerHandler", new ServerHandler(async (contenxt, message) =>
                {
                    var sender = new DotNettyServerMessageSender(_transportMessageEncoder, contenxt);
                    if (message.IsInvokeMessage())
                    {
                        var invokeMessage = message.GetContent<RemoteInvokeMessage>();
                        if (invokeMessage.ServiceId == "client.checkService")
                        {
                            await sender.SendAndFlushAsync(TransportMessage.CreateInvokeResultMessage(message.Id, new RemoteInvokeResultMessage()));
                            return;
                        }
                    }
                    await OnReceived(sender, message);
                }, _logger));
            }));
            try
            {
                _channel = await bootstrap.BindAsync(endPoint);
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug($"服务主机启动成功，监听地址：{endPoint}。");
            }
            catch
            {
                _logger.LogError($"服务主机启动失败，监听地址：{endPoint}。 ");
            }
        }

        public void CloseAsync()
        {
            Task.Run(async () =>
            {
                await _channel.EventLoop.ShutdownGracefullyAsync();
                await _channel.CloseAsync();
            }).Wait();
        }

        #region Implementation of IDisposable

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            Task.Run(async () =>
            {
                await _channel.DisconnectAsync();
            }).Wait();
        }

        #endregion Implementation of IDisposable

        #region Help Class

        private class ServerHandler : ChannelHandlerAdapter
        {
            private readonly Action<IChannelHandlerContext, TransportMessage> _readAction;
            private readonly ILogger _logger;

            public ServerHandler(Action<IChannelHandlerContext, TransportMessage> readAction, ILogger logger)
            {
                _readAction = readAction;
                _logger = logger;
            }

            #region Overrides of ChannelHandlerAdapter
            [MethodImpl(MethodImplOptions.NoInlining)]

            public override void ChannelRead(IChannelHandlerContext context, object message)
            {
                var transportMessage = message as TransportMessage;
                _readAction(context, transportMessage);
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public override void ChannelReadComplete(IChannelHandlerContext context)
            {
                context.Flush();
            }

            [MethodImpl(MethodImplOptions.NoInlining)]
            public override void ExceptionCaught(IChannelHandlerContext context, Exception exception)
            {
                context.CloseAsync();//客户端主动断开需要应答，否则socket变成CLOSE_WAIT状态导致socket资源耗尽
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(exception, $"与服务器：{context.Channel.RemoteAddress}通信时发送了错误。");
            }

            #endregion Overrides of ChannelHandlerAdapter
        }

        #endregion Help Class
    }
}