﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bot.Builder.ChannelConnector.Directline;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;

namespace Bot.Builder.ChannelConnector.Owin.DirectLine
{
	public class DirectlineWebSocketHandler : IChatLogListener
	{
		public static JsonSerializerSettings JsonSerializerSettings { get; set; }

		public static TimeSpan TokenExpirationTime = TimeSpan.FromSeconds(1800);

		static readonly ConcurrentDictionary<string, DirectlineWebSocketHandler> HandlersByToken
			= new ConcurrentDictionary<string, DirectlineWebSocketHandler>();

		public static DirectlineWebSocketHandler GetByToken(string token)
		{
			// Token to be used only one time to establish connection

			if (HandlersByToken.TryRemove(token, out DirectlineWebSocketHandler handler))
			{
				if (!handler.IsTokeExpired)
				{
					return handler;
				}
			}
			return null;
		}

		public static DirectlineWebSocketHandler Create(string conversationId, IChatLog chatLog)
		{
			var handler = new DirectlineWebSocketHandler(conversationId, chatLog);
			HandlersByToken.TryAdd(handler.Token, handler);

			ExpireTokens();

			return handler;
		}

		static void ExpireTokens()
		{
			var expiredHandlers = HandlersByToken.Values.Where(h => h.IsTokeExpired).ToList();
			foreach (var expiredHandler in expiredHandlers)
			{
				HandlersByToken.TryRemove(expiredHandler.Token, out DirectlineWebSocketHandler ignore);
			}
		}

		OwinWebSocket webSocket;

		public string Token { get; }
		public DateTime DateIssued { get; }
		public string ConversationId { get; }
		public IChatLog ChatLog { get; }

		int Watermark { get; set; }

		public bool IsTokeExpired => DateTime.UtcNow > DateIssued.Add(TokenExpirationTime);

		DirectlineWebSocketHandler(string conversationId, IChatLog chatLog)
		{
			Token = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
			DateIssued = DateTime.UtcNow;
			ChatLog = chatLog;
			ConversationId = conversationId;
			Watermark = 0;
		}

		public async Task OpenAsync(OwinWebSocket webSocket)
		{
			this.webSocket = webSocket;

			Trace.TraceInformation("Opening web socket connection");

			ChatLog.AddListener(this);

			var activities = (await ChatLog.GetActivitiesAsync(ConversationId)).Skip(Watermark).ToArray();
			await SendActivitiesAsync(activities);
		}

		public void Close()
		{
			Trace.TraceInformation("Websocket closed");
			ChatLog.RemoveListener(this);
			webSocket = null;
		}

		async Task SendActivitiesAsync(Activity[] activities, bool addWatermark = true)
		{
			if (webSocket == null) return;

			Watermark = Watermark + activities.Length;
			var result = new
			{
				Activities = activities,
				Watermark = addWatermark ? Watermark : (int?)null
			};

			var json = JsonConvert.SerializeObject(result, JsonSerializerSettings);
			var bytes = Encoding.UTF8.GetBytes(json);
			try
			{
				await webSocket.SendAsync
				(
					new ArraySegment<byte>(bytes, 0, bytes.Length),
					WebSocketMessageType.Text,
					true,
					CancellationToken.None
				);
			}
			catch (Exception e)
			{
				Trace.TraceError($"Error sending to websocket {e}");
				throw;
			}
		}

		public async void OnActivity(Activity activity)
		{
			if (activity.Conversation.Id == ConversationId)
			{
				await SendActivitiesAsync(new [] { activity });
			}
		}
	}
}