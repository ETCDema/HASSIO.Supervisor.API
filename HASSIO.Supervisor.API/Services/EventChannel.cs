using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

using Dm.Data.UM;
using Dm.Data.UM.MapSources;

using Microsoft.Extensions.Logging;

namespace HASSIO.Supervisor.API.Services
{
	/// <summary>
	/// Канал получения событий от SupervisorAPI и отправки ему команд.
	/// <para>
	/// Создает 2 потока: чтение сообщений от API и добавление в очередь на обработку и обработка очереди поступивших сообщений.
	/// </para>
	/// </summary>
	internal class EventChannel
	{
		private readonly Uri _endpoint;
		private readonly ILogger<EventChannel> _log;

		private readonly Dictionary<string, MsgHandler> _handlers = [];

		private BufferBlock<string> _queue;
		private _states _state;
		private SendMessageFx _sendFx;

		/// <summary>
		/// Асинхронный метод отправки команд
		/// </summary>
		/// <param name="json">JSON команды</param>
		/// <returns></returns>
		public delegate Task SendMessageFx(string json);

		/// <summary>
		/// Асинхронный обработчик сообщения
		/// </summary>
		/// <param name="id">Идентификатор команды или -1 если событие не относится к команде</param>
		/// <param name="msgType">Тип сообщения</param>
		/// <param name="src">Данные сообщения после id и type</param>
		/// <param name="sendFx">Асинхронный метод отправки команд</param>
		/// <returns></returns>
		public delegate Task MsgHandler(int id, string msgType, IMapSource src, SendMessageFx sendFx);

		public EventChannel(Uri endpoint, ILogger<EventChannel> log)
		{
			_endpoint			= endpoint;
			_log				= log;
			_sendFx             = _dummySend;

			_queue				= new();
			_state				= _states.Created;
		}

		/// <summary>
		/// Добавить обработчик типа сообщения
		/// </summary>
		/// <param name="msgType">Тип сообщения</param>
		/// <param name="handler">Асинхронный обработчик сообщения</param>
		/// <returns>Этот же экземпляр</returns>
		public EventChannel AddHandler(string msgType, MsgHandler handler)
		{
			if (_state!=_states.Created) throw new InvalidOperationException($"AddHandler not allowed - allowed on Created state, actual state {_state}");
			_handlers.Add(msgType, handler);
			return this;
		}

		/// <summary>
		/// Запустить работу канала
		/// </summary>
		/// <param name="cancel"></param>
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		public Task Run(CancellationToken cancel)
		{
			_log.LogInformation("Starting...");
			if (Interlocked.CompareExchange(ref _state, _states.Starting, _states.Created)!=_states.Created)
				throw new InvalidOperationException($"Start not allowed, current state {_state}");

			// Сначала запустим прослушивателя очереди т.к. после подключения события попрут сразу
			_log.LogInformation("Run event handler");
			var task1			= Task.Run(() => _handleEvent(cancel), CancellationToken.None);

			// Запускаем соединение и прослушивание канала
			_log.LogInformation("Start channel interaction");
			var task2			= Task.Run(() => _connectAndRecieve(cancel), CancellationToken.None);

			_log.LogInformation("Start completed");
			return Task.WhenAll(task1, task2).ContinueWith((t) =>
			{
				Interlocked.Exchange(ref _queue, new())?.Complete();
				Interlocked.Exchange(ref _state, _states.Created);
			}, CancellationToken.None);
		}

		/// <summary>
		/// Поток получения сообщений и добавление в очередь для обработки, автоматически пытается переподключиться при разрыве связи с интервалом 10 секунд
		/// </summary>
		/// <param name="cancel"></param>
		/// <returns></returns>
		private async Task _connectAndRecieve(CancellationToken cancel)
		{
			while (!cancel.IsCancellationRequested)
			{
				Interlocked.Exchange(ref _state, _states.RecieveStarting);
				using var ws    = new ClientWebSocket();
				ws.Options.KeepAliveInterval	= TimeSpan.FromSeconds(30);
				ws.Options.KeepAliveTimeout		= TimeSpan.FromSeconds(20);

				try
				{
					_log.LogInformation("Connecting to {endpoint}...", _endpoint);
					await ws.ConnectAsync(_endpoint, cancel);

					Interlocked.Exchange(ref _sendFx, (json) => _send(ws, json, cancel));

					_log.LogInformation("Recieve events");
					Interlocked.Exchange(ref _state, _states.RecieveStarted);
					while (!cancel.IsCancellationRequested)
					{
						var data		= await _recieve(ws, cancel);
						if (!string.IsNullOrEmpty(data) && _queue.Post(data)) _log.LogDebug("📥 {data}", data);
					}
					Interlocked.Exchange(ref _sendFx, _dummySend);
				} catch (TaskCanceledException)
				{
					Interlocked.Exchange(ref _sendFx, _dummySend);
					Interlocked.Exchange(ref _state, _states.Cancelled);
					_log.LogError("Connect and recieve cancelled");
					if (ws.State==WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutdown", default);
					return;
				} catch (WebSocketException wse)
				{
					Interlocked.Exchange(ref _sendFx, _dummySend);
					Interlocked.Exchange(ref _state, _states.RecieveStartingRetry);
					var delay	= TimeSpan.FromSeconds(10);
					_log.LogError(wse, "Connect and recieve exception, try restart after {time}", delay);
					await Task.Delay(delay, cancel);
				}
			}
		}

		/// <summary>
		/// Поток обработки сообщений с использованием зарегистрированных обработчиков
		/// </summary>
		/// <param name="cancel"></param>
		/// <returns></returns>
		private async Task _handleEvent(CancellationToken cancel)
		{
			Interlocked.CompareExchange(ref _state, _states.HandleEventsReady, _states.Starting);

			while (!cancel.IsCancellationRequested && await _queue.OutputAvailableAsync(cancel))
			{
				var data        = _queue.Receive();
				var logData     = data.Length> 128 ? data[..128]+"..." : data;
				_log.LogDebug("⚙ {data}", logData);
				try
				{
					using var src       = new JSONSource(JsonDocument.Parse(data).RootElement);

					if (!src.GetNextProp()) throw new FormatException(src.GetInfo());
					if (src.NodeType!=MapNode.Property) throw new FormatException($"Required id or type property, but get {src.NodeName}: {src.NodeType} at {src.GetInfo()}");

					var id      = -1;
					if (src.NodeName=="id")
					{
						id      = src.GetData<int>();

						if (!src.GetNextProp()) throw new FormatException(src.GetInfo());
						if (src.NodeType!=MapNode.Property || src.NodeName!="type") throw new FormatException($"Required type property, but get {src.NodeName}: {src.NodeType} at {src.GetInfo()}");
					}

					var eventType		= src.GetData<string>();
					if (string.IsNullOrEmpty(eventType))
					{
						_log.LogWarning("Empty event type");
					} else if (_handlers.TryGetValue(eventType, out var handler))
					{
						// Есть обработчик типа сообщения - завем его
						await handler(id, eventType, src, _sendFx);
					} else
					{
						_log.LogInformation("No handler for event type {eventType}", eventType);
					}
				} catch (Exception ex)
				{
					_log.LogError(ex, "Handle event {data} FAIL", logData);
				}
			}
		}

		/// <summary>
		/// Получение сообщений. Собирвает сообщения из фрагментов и возвращает уже целое сообщение.
		/// </summary>
		/// <param name="ws"></param>
		/// <param name="cancel"></param>
		/// <returns></returns>
		private async Task<string?> _recieve(ClientWebSocket ws, CancellationToken cancel)
		{
			var res             = new MemoryStream();

			var bytes           = new byte[1024];
			var result          = default(WebSocketReceiveResult);
			do
			{
				result			= await ws.ReceiveAsync(bytes, cancel);
				if (cancel.IsCancellationRequested)
				{
					_log.LogWarning("Recieve cancelled: {state}", ws.State);
					return null;
				}
				res.Write(bytes, 0, result.Count);
			} while (!result.EndOfMessage);

			var json            = Encoding.UTF8.GetString(res.ToArray());
			return json;
		}

		/// <summary>
		/// Отправка сообщения
		/// </summary>
		/// <param name="ws">Транспорт</param>
		/// <param name="v">Сообщение</param>
		/// <param name="cancel"></param>
		/// <returns></returns>
		private async Task _send(ClientWebSocket ws, string v, CancellationToken cancel)
		{
			_log.LogDebug("📤 {v}...", v);
			try
			{
				await ws.SendAsync(Encoding.UTF8.GetBytes(v), WebSocketMessageType.Text, true, cancel);
				_log.LogDebug("📤 Send done");
			} catch (OperationCanceledException cex)
			{
				_log.LogWarning(cex, "Send cancelled: {state}", ws.State);
			} catch (Exception ex)
			{
				_log.LogWarning(ex, "Send {v} FAIL", v);
			}
		}

		/// <summary>
		/// Заглушка отправки сообщения
		/// </summary>
		/// <param name="json"></param>
		/// <returns></returns>
		private Task _dummySend(string json)
		{
			_log.LogWarning("Data {json} not sended: WebSocketClient not ready", json);
			return Task.CompletedTask;
		}

		/// <summary>
		/// Сосотояния канала
		/// </summary>
		private enum _states
		{
			/// <summary>Создан - можно добавлять обработчики и запускать работу</summary>
			Created				= 0,

			/// <summary>Начали запуск</summary>
			Starting,

			/// <summary>Создали поток обработки сообщений</summary>
			HandleEventsReady,

			/// <summary>Начинаем подключаться</summary>
			RecieveStarting,

			/// <summary>Пробуем переподключаться</summary>
			RecieveStartingRetry,

			/// <summary>Подключение создано и активно</summary>
			RecieveStarted,

			/// <summary>Получение и отправка прерваны</summary>
			Cancelled,
		}
	}
}
