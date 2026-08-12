using MeterBridge.Mqtt;
using MeterBridge.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<MqttPublisher>();
builder.Services.AddHostedService<HuaweiModbusService>();
builder.Services.AddHostedService<GasMeterService>();
builder.Services.AddHostedService<StromzaehlerService>();
builder.Services.AddHostedService<HomeAssistantDiscoveryService>();

var host = builder.Build();
await host.RunAsync();
