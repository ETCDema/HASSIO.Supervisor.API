using System.Threading;
using System.Threading.Tasks;

using HASSIO.Supervisor.API.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HASSIO.Supervisor.API
{
	public static class HASSIOSupervisorCoreAPI
	{
		public static IServiceCollection AddHASSIOSuperviserAPI(this IServiceCollection services, string accessToken, string? endpoint = null)
		{
			return services.AddSingleton(sp => new HASSIOSupervisorCore(
												sp.GetRequiredService<ILoggerFactory>(),
												sp.GetServices<IDeviceService>(),
												accessToken,
												endpoint));
		}

		public static async Task RunHASSIOSupervisorCoreAsync(this IHost host)
		{
			// Тут выполняется инициализация обработчиков остановки приложения
			await host.Services.GetRequiredService<IHostLifetime>().WaitForStartAsync(CancellationToken.None);

			// Запускаем сервис
			await host.Services
					   .GetRequiredService<HASSIOSupervisorCore>()
					   .RunAsync(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
		}
	}
}
