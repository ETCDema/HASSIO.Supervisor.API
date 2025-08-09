using Dm.Data.UM;

namespace HASSIO.Supervisor.API.Services
{
	/// <summary>
	/// API устройств для обновления состояния
	/// </summary>
	public interface IDeviceService
	{
		/// <summary>
		/// Получить коллекцию отслеживаемых entityID
		/// </summary>
		/// <returns></returns>
		string[] GetListeningEntities();

		/// <summary>
		/// Создать экземпляр подготовки и применения обновлений
		/// </summary>
		/// <returns></returns>
		IDeviceStateUpdater NewUpdater();
	}

	/// <summary>
	/// API объекта подготовки и применения обновлений
	/// </summary>
	public interface IDeviceStateUpdater
	{
		/// <summary>
		/// Подготовить обновление
		/// </summary>
		/// <param name="entityID">Идентификатор обновляемого entity</param>
		/// <param name="src">Данные состояния entity</param>
		/// <returns></returns>
		bool TryMap(string entityID, IMapSource src);

		/// <summary>
		/// Применить подготовленные обновления
		/// </summary>
		void Update();
	}
}
