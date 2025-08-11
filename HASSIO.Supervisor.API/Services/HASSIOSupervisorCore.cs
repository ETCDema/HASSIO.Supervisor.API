using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Dm.Data.UM;
using Dm.Data.UM.MapTargets;

using HASSIO.Supervisor.API.Models;

using Microsoft.Extensions.Logging;

namespace HASSIO.Supervisor.API.Services
{
	/// <summary>
	/// Реализация работы с Home Assistant Superviser API
	/// </summary>
	internal class HASSIOSupervisorCore
	{
		private readonly ILoggerFactory _lf;
		private readonly DevicesUpdaters _devicesUpdaters;
		private readonly Uri _endpoint;
		private readonly string _accessToken;

		private readonly ILogger<HASSIOSupervisorCore> _log;
		private int _cmdId;
		private readonly ConcurrentDictionary<int, ResultHandler> _requestMap = [];

		/// <summary>
		/// Асинхронный обработчик результата выполнения команды
		/// </summary>
		/// <param name="src">Данные результата</param>
		/// <param name="sendFx">Асинхронный метод отправки команды</param>
		/// <returns></returns>
		private delegate Task ResultHandler(IMapSource src, EventChannel.SendMessageFx sendFx);

		public HASSIOSupervisorCore(ILoggerFactory lf, IEnumerable<IDeviceService> devices, string accessToken, string? endpoint = null)
		{
			if (string.IsNullOrEmpty(endpoint)) endpoint = "ws://supervisor/core/websocket";
			if (string.IsNullOrEmpty(accessToken)) throw new ArgumentNullException(nameof(accessToken));

			_lf					= lf; 
			_log				= lf.CreateLogger<HASSIOSupervisorCore>();
			_devicesUpdaters	= new(devices);

			_endpoint			= new Uri(endpoint);
			_accessToken        = accessToken;
		}

		/// <summary>
		/// Запуск работы с HASSIO API
		/// </summary>
		/// <param name="cancel"></param>
		/// <returns></returns>
		public Task RunAsync(CancellationToken cancel)
		{
			if (_devicesUpdaters.ListeningEntities.Count==0)
			{
				_log.LogWarning("No device updaters - exit");
				return Task.CompletedTask;
			}

			var myCancel        = CancellationTokenSource.CreateLinkedTokenSource(cancel);
			var channel         = new EventChannel(_endpoint, _lf.CreateLogger<EventChannel>());

			return channel
				.AddHandler("auth_required",	_authRequired)
				.AddHandler("auth_ok",			_authOk)
				.AddHandler("auth_invalid",		async (id, t, src, fx) =>
				{
					await _authInvalid(id, t, src, fx);
					myCancel.Cancel();
				})
				.AddHandler("result",			_requestResult)
				.AddHandler("event",			_triggerEvent)
				.Run(myCancel.Token)
				.ContinueWith(t =>
				{
					_log.LogInformation("🛑 Service stopped");
				});
		}

		#region Auth on connect

		/// <summary>
		/// Обработка запроса авторизации
		/// </summary>
		/// <inheritdoc cref="EventChannel.MsgHandler"/>
		private Task _authRequired(int id, string msgType, IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			// На запрос авторизации нужно отправит access token
			return sendFx(JSONTarget.Create(t => {
				t.Data("type", "auth")
				 .Data("access_token", _accessToken);
			}));
		}

		/// <summary>
		/// Обработка успешного завершения авторизации
		/// </summary>
		/// <inheritdoc cref="EventChannel.MsgHandler"/>
		private Task _authOk(int id, string msgType, IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			_log.LogInformation("Auth Ok");

			// Если есть что слушать - запрашиваем текущие состояния устройств, к сожалению получить отдельные невозможно
			return _devicesUpdaters.ListeningEntities.Count>0
				 ? sendFx(JSONTarget.Create(t =>
					{
						_newCommand("get_states", t, _getStatesResult);
					}))
				 : Task.CompletedTask;
		}

		/// <summary>
		/// Обработка проблем авторизации
		/// </summary>
		/// <inheritdoc cref="EventChannel.MsgHandler"/>
		private Task _authInvalid(int id, string msgType, IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			var message         = "No message";
			while (src.GetNextProp())
			{
				if (src.NodeName!="message") continue;

				message         = src.GetData<string>() ?? message;
				break;
			}

			_log.LogCritical("Auth invalid: {message}", message);
			return Task.CompletedTask;
		}

		#endregion

		#region Command request and handle request result

		/// <summary>
		/// Инициализация данных новой команды
		/// </summary>
		/// <param name="cmd">Команда</param>
		/// <param name="t">Куда записать нужные данные</param>
		/// <param name="handler">Обработчик ответа</param>
		private void _newCommand(string cmd, IMapTarget t, ResultHandler handler)
		{
			var id              = Interlocked.Increment(ref _cmdId);
			t.Data("id",	id)
			 .Data("type",	cmd);

			_requestMap.TryAdd(id, handler);
			_log.LogInformation("New request {id}: {cmd}", id, cmd);
		}

		/// <summary>
		/// Обработка результата команды
		/// </summary>
		/// <inheritdoc cref="EventChannel.MsgHandler"/>
		private async Task _requestResult(int id, string msgType, IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			_requestMap.TryRemove(id, out var fx);

			while (src.GetNextProp())
			{
				switch (src.NodeName)
				{
					case "success":
						if (!src.GetData<bool>())
						{
							var message = "No message";
							_mapObject(src, "error", src =>
							{
								while (src.GetNextProp())
								{
									if (src.NodeName=="message")
									{
										message = src.GetData<string>() ?? message;
										return;
									}
									src.Skip();
								}
							});
							_log.LogError("Unsuccess result recieved for request {id}: {message}", id, message);
							return;
						}
						break;

					case "result":
						if (fx!=null)
						{
							await fx(src, sendFx);
						} else
						{
							_log.LogWarning("Handler for request {id} not exists", id);
						}
						return;

					default: src.Skip(); break;
				}
			}
		}

		/// <summary>
		/// Пустой обработчик результата
		/// </summary>
		/// <param name="src"></param>
		/// <param name="sendFx"></param>
		/// <returns></returns>
		private Task _ignoreResult(IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			return Task.CompletedTask;
		}

		#endregion

		#region Get states command result

		/// <summary>
		/// Отработка команды получения состояний get_states
		/// </summary>
		/// <inheritdoc cref="ResultHandler"/>
		private Task _getStatesResult(IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			// К нам приходит начало массива
			if (src.IsNull()) return Task.CompletedTask;
			if (src.NodeType!=MapNode.StartProperty) throw new FormatException("Array expected at "+src.GetInfo());

			var activeUpdates   = _devicesUpdaters.NewUpdates(w => { });

			while (src.GetNextProp())
			{
				if (src.IsNull()) continue;
				if (src.NodeType!=MapNode.StartProperty || src.NodeName!=null) throw new FormatException("Entity not found at "+src.GetInfo());

				// Пробуем собрать данные для entity
				activeUpdates.TryMap(src);

				if (src.NodeType!=MapNode.EndProperty)
				{
					// Объект не дочитали до конца - сделаем это сами
					while (src.GetNextProp())
					{
						src.Skip();
					}
				}
			}

			// Применяем собранные изменения
			activeUpdates.Update();

			// Подписываемся на события изменения состояния известных entity
			return sendFx(JSONTarget.Create(t =>
			{
				_newCommand("subscribe_trigger", t, _ignoreResult);
				t.Start("trigger")
					.Data("platform",	"state")
					.Array("entity_id", _devicesUpdaters.ListeningEntities)
				 .End();
			}));
		}

		#endregion

		#region Trigger event processing

		/// <summary>
		/// Обработка событий триггеров
		/// </summary>
		/// <inheritdoc cref="EventChannel.MsgHandler"/>
		private Task _triggerEvent(int id, string msgType, IMapSource src, EventChannel.SendMessageFx sendFx)
		{
			_mapObject(src, "event", src =>
			{
				_mapObject(src, "variables", src =>
				{
					_mapObject(src, "trigger", src =>
					{
						_mapObject(src, "to_state", src =>
						{
							var activeUpdates   = _devicesUpdaters.NewUpdates(w => _log.LogWarning(w));

							if (activeUpdates.TryMap(src)) activeUpdates.Update();
						});
					});
				});
			});

			return Task.CompletedTask;
		}

		/// <summary>
		/// Получение данных объекта
		/// </summary>
		/// <param name="src">Поток данных</param>
		/// <param name="name">Имя свойтсва, в котором лежит нужный объект</param>
		/// <param name="fx">Обработчик данных объекта</param>
		private void _mapObject(IMapSource src, string name, Action<IMapSource> fx)
		{
			while (src.GetNextProp())
			{
				if (src.NodeType==MapNode.StartProperty && src.NodeName==name)
					fx(src);
				else
					src.Skip();
			}
		}

		#endregion
	}
}
