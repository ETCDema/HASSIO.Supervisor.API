using System;
using System.Collections.Generic;
using System.Collections.Immutable;

using Dm.Data.UM;

using HASSIO.Supervisor.API.Services;

namespace HASSIO.Supervisor.API.Models
{
	/// <summary>
	/// Коллекция зарегистрированных сервисов IDeviceService для обновления статусов устройств
	/// </summary>
	internal class DevicesUpdaters
	{
		/// <summary>
		/// Все доступные методы создания экземпляра для обновления состояния устройства с привязкой к entityID
		/// </summary>
		private readonly Dictionary<string, _deviceUpdater> _updateProducers = [];

		/// <summary>
		/// Кэш созданных экземпляров обновления состояния устройств
		/// </summary>
		internal interface IActiveUpdaters
		{
			/// <summary>
			/// Создать при необходимости новый экземпляр обновления состояния на основе entity_id из src и подготовить обновление.
			/// </summary>
			/// <remarks>Может вызываться несколько раз, зависит от данных в исходном сообщении</remarks>
			/// <param name="src">Данные entity</param>
			/// <returns>true - были подготовлены изменения</returns>
			bool TryMap(IMapSource src);

			/// <summary>
			/// Применить подготовленные обновления
			/// </summary>
			void Update();
		}

		public DevicesUpdaters(IEnumerable<IDeviceService> devices)
		{
			foreach (var device in devices)
			{
				var entities    = device.GetListeningEntities().ToImmutableList();
				foreach (var entity in entities)
				{
					_updateProducers.Add(entity, new _deviceUpdater(entities, device.NewUpdater));
				}
			}

			ListeningEntities	= [.._updateProducers.Keys];
		}

		/// <summary>
		/// Идентификаторы всех отслеживаемых entity
		/// </summary>
		public IReadOnlyList<string> ListeningEntities	{ get; }

		/// <summary>
		/// Создать новый кэш экземпляров
		/// </summary>
		/// <param name="warning">Метод обработки предупреждений</param>
		/// <returns></returns>
		public IActiveUpdaters NewUpdates(Action<string> warning)
		{
			return new _activeUpdaters(_updateProducers, warning);
		}

		/// <summary>
		/// Данные для создания экземпляра обновления состояния устройства
		/// </summary>
		/// <param name="EntityIDs">Коллекция EntitiID, которые экземпляр может обработать</param>
		/// <param name="NewUpdaterFx">Метод создания экземпляра</param>
		private record _deviceUpdater(IList<string> EntityIDs, Func<IDeviceStateUpdater> NewUpdaterFx);

		/// <summary>
		/// Реализация кэша экземпляров обновления состояния устройств
		/// </summary>
		private class _activeUpdaters: IActiveUpdaters
		{
			private readonly Dictionary<string, _deviceUpdater> _updateProducers;
			private readonly Action<string> _warning;
			private readonly Dictionary<string, IDeviceStateUpdater> _activeByEntity;
			private readonly List<IDeviceStateUpdater> _active;

			internal _activeUpdaters(Dictionary<string, _deviceUpdater> updateProducers, Action<string> warning)
			{
				_updateProducers		= updateProducers;
				_warning				= warning;
				_activeByEntity         = [];
				_active                 = [];
			}

			/// <inheritdoc/>
			public bool TryMap(IMapSource src)
			{
				if (src.IsNull()) return false;
				if (src.NodeType!=MapNode.StartProperty) throw new FormatException($"Expected NodeType=StartProperty? but actual is {src.NodeType} at {src.GetInfo()}");
				if (!src.GetNextProp() || src.NodeType!=MapNode.Property || src.NodeName!="entity_id") throw new FormatException($"Expected entity_id property but found {src.NodeName}: {src.NodeType} at {src.GetInfo()}");

				// entity_id должно идти первым элементом
				var entityId    = src.GetData<string>() ?? "";

				if (!_activeByEntity.TryGetValue(entityId, out var updater))
				{
					// Если нет в кэше - пробуем создать
					if (!_updateProducers.TryGetValue(entityId, out var updaterProducer))
					{
						// Нет в коллекции доступных, это странная ситуация
						_warning($"No updaters for entity {entityId}");
						return false;
					}

					// Создаем и кэшируем экземпляр
					updater     = updaterProducer.NewUpdaterFx();
					foreach (var entity in updaterProducer.EntityIDs)
					{
						_activeByEntity.Add(entity, updater);
					}
					_active.Add(updater);
				}

				// Пытаемся подготовить обновление по полученным данным
				return updater.TryMap(entityId, src);
			}

			/// <inheritdoc/>
			public void Update()
			{
				foreach (var updater in _active) updater.Update();
			}
		}
	}
}
