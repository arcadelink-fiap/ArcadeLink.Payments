using ArcadeLink.Payments.Api.Consumers;
using ArcadeLink.Payments.Api.Health;
using ArcadeLink.Payments.Api.Messaging;
using ArcadeLink.Payments.Api.Options;
using MassTransit;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<PaymentsOptions>(builder.Configuration.GetSection(PaymentsOptions.SectionName));

builder.Services.AddStackExchangeRedisCache(options =>
    options.Configuration = builder.Configuration["Redis:ConnectionString"]);
builder.Services.AddSingleton<IApprovalRateProvider, CachedApprovalRateProvider>();

// Prefixo "payments" por consistência e para evitar colisão futura de nomes de consumer (ver AddArcadeLinkMessaging).
builder.Services.AddArcadeLinkMessaging(builder.Configuration, "payments", x => x.AddConsumer<OrderPlacedEventConsumer>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Payments is purely event-driven today: it exposes no authenticated HTTP endpoint, so it carries no
// authentication/authorization wiring at all. Any authenticated endpoint added here in the future
// must go through the API Gateway (Kong) and read the X-User-Id/X-User-Role headers it injects,
// following the KongHeader scheme used by Users and Catalog.
app.MapGet("/health", () => Results.Ok(HealthInfo.Create(app.Configuration, "payments-api")))
    .WithName("Health")
    .WithOpenApi();

app.Run();
