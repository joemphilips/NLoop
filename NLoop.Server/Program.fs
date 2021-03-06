namespace NLoop.Server

open System
open System.CommandLine.Builder
open System.CommandLine.Invocation
open System.CommandLine.Hosting
open System.CommandLine.Parsing
open System.IO
open System.Net
open System.Security.Cryptography.X509Certificates
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Options
open Microsoft.IO
open Microsoft.Extensions.Hosting

open Microsoft.AspNetCore.Authentication.Certificate
open Microsoft.AspNetCore.Authentication.Cookies
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Cors.Infrastructure
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core

open Giraffe

open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.LoopHandlers
open NLoop.Server.Services

open FSharp.Control.Tasks.Affine

module App =
  let noCookie: HttpHandler =
    RequestErrors.UNAUTHORIZED
      "Basic"
      "Access to the protected API"
      "You must authenticate with cookie or certificate"

  let mustAuthenticate =
    // TODO: perform real authentication
    fun (next: HttpFunc) (ctx: HttpContext) -> next ctx
    // requiresAuthentication noCookie

  let webApp =
    choose [
      subRoutef "/v1/%s" (fun cryptoCode ->
        choose [
          POST >=>
            route "/loop/out" >=> mustAuthenticate >=>
              bindJsonWithCryptoCode<LoopOutRequest> cryptoCode (handleLoopOut)
            route "/loop/in" >=> mustAuthenticate >=>
              bindJsonWithCryptoCode<LoopInRequest> cryptoCode (handleLoopIn)
      ])
      subRoute "/v1" (choose [
        GET >=>
          route "/info" >=> QueryHandlers.handleGetInfo
          route "/version" >=> json Constants.AssemblyVersion
          route "/events" >=> QueryHandlers.handleListenEvent
        ])
      setStatusCode 404 >=> text "Not Found"
    ]

  // ---------------------------------
  // Error handler
  // ---------------------------------

  let errorHandler (ex : Exception) (logger : ILogger) =
      logger.LogError(ex, "An unhandled exception has occurred while executing the request.")
      clearResponse >=> setStatusCode 500 >=> text ex.Message

  // ---------------------------------
  // Config and Main
  // ---------------------------------

  let configureCors (opts: NLoopOptions) (builder : CorsPolicyBuilder) =
      builder
          .WithOrigins(opts.RPCCors)
         .AllowAnyMethod()
         .AllowAnyHeader()
         |> ignore

  let configureApp (app : IApplicationBuilder) =
      let env = app.ApplicationServices.GetService<IWebHostEnvironment>()
      let opts = app.ApplicationServices.GetService<IOptions<NLoopOptions>>().Value
      (match env.IsDevelopment() with
      | true  ->
          app
            .UseDeveloperExceptionPage()
            .UseMiddleware<RequestResponseLoggingMiddleware>()
      | false ->
          app
            .UseGiraffeErrorHandler(errorHandler))
            .UseCors(configureCors opts) |> ignore

      app
        .UseAuthentication()
        .UseGiraffe(webApp)

  let configureServices test (env: IHostEnvironment) (services : IServiceCollection) =

      // json settings
      let jsonOptions = JsonSerializerOptions()
      jsonOptions.AddNLoopJsonConverters()
      services
        .AddSingleton(jsonOptions)
        .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(jsonOptions)) |> ignore // for giraffe

      services.AddNLoopServices(test) |> ignore

      if (env.IsDevelopment()) then
        services.AddTransient<RequestResponseLoggingMiddleware>() |> ignore
        services.AddSingleton<RecyclableMemoryStreamManager>() |> ignore
      else
        ()

      services.AddCors()    |> ignore

      services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCertificate(fun o -> o.AllowedCertificateTypes <- CertificateTypes.SelfSigned)
        .AddCertificateCache()
        .AddCookie(fun _o -> ())
        |> ignore

      services.AddGiraffe() |> ignore


type Startup(conf: IConfiguration, env: IHostEnvironment) =
  member this.Configure(appBuilder) =
    App.configureApp(appBuilder)

  member this.ConfigureServices(services) =
    App.configureServices false env services

module Main =

  let configureLogging (builder : ILoggingBuilder) =
      builder
        .AddConsole()
        .AddDebug()
#if DEBUG
        .SetMinimumLevel(LogLevel.Debug)
#else
        .SetMinimumLevel(LogLevel.Information)
#endif
        |> ignore

  let configureConfig (builder: IConfigurationBuilder) =
    builder.SetBasePath(Directory.GetCurrentDirectory()) |> ignore
    Directory.CreateDirectory(Constants.HomeDirectoryPath) |> ignore
    let iniFile = Path.Join(Constants.HomeDirectoryPath, "nloop.conf")
    if (iniFile |> File.Exists) then
      builder.AddIniFile(iniFile) |> ignore
    builder.AddEnvironmentVariables(prefix="NLOOP_") |> ignore
    ()

  let configureHostBuilder (hostBuilder: IHostBuilder) =
    hostBuilder.ConfigureAppConfiguration(configureConfig)
      .ConfigureWebHostDefaults(
        fun webHostBuilder ->
          webHostBuilder
            .UseStartup<Startup>()
            .UseUrls()
            .UseKestrel(fun kestrelOpts ->
              let opts = kestrelOpts.ApplicationServices.GetRequiredService<IOptions<NLoopOptions>>().Value
              let logger = kestrelOpts.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger<Startup>()

              let ipAddresses = ResizeArray<_>()
              match opts.RPCHost |> IPAddress.TryParse with
              | true, ip ->
                ipAddresses.Add(ip)
              | false, _ when opts.RPCHost = Constants.DefaultRPCHost ->
                ipAddresses.Add(IPAddress.IPv6Loopback)
                ipAddresses.Add(IPAddress.Loopback)
              | _ ->
                ipAddresses.Add(IPAddress.IPv6Any)

              if (opts.NoHttps) then
                for ip in ipAddresses do
                  logger.LogInformation($"Binding to http://{ip}")
                  kestrelOpts.Listen(ip, port = opts.RPCPort, configure=fun (s: ListenOptions) -> s.UseConnectionLogging() |> ignore)
              else
                for ip in ipAddresses do
                  logger.LogInformation($"Binding to https://{ip}")
                  let cert = new X509Certificate2(opts.HttpsCert, opts.HttpsCertPass)
                  kestrelOpts.Listen(ip, port = opts.HttpsPort, configure=(fun (s: ListenOptions) ->
                    s.UseConnectionLogging().UseHttps(cert) |> ignore))
              )
            .ConfigureLogging(configureLogging)
            |> ignore
      )

  /// Mostly the same with `UseHost`, but it will call `IHost.RunAsync` instead of `StartAsync`,
  /// thus it never finishes.
  /// We need this because we want to bind the CLI options into <see cref="NLoop.Server.NLoopOptions"/> with
  /// `BindCommandLine`, which requires `BindingContext` injected in a DI container.
  let useWebHostMiddleware = InvocationMiddleware(fun ctx next -> unitTask {
    let hostBuilder = HostBuilder()
    hostBuilder.Properties.[typeof<InvocationContext>] <- ctx

    hostBuilder.ConfigureServices(fun (services: IServiceCollection) ->
      services
        .AddSingleton(ctx)
        .AddSingleton(ctx.BindingContext)
        .AddSingleton(ctx.Console)
        .AddTransient<_>(fun _ -> ctx.InvocationResult)
        .AddTransient<_>(fun _ -> ctx.ParseResult)
      |> ignore
    )
      .UseInvocationLifetime(ctx)
      |> ignore
    configureHostBuilder hostBuilder |> ignore

    use host = hostBuilder.Build();
    ctx.BindingContext.AddService(typeof<IHost>, fun _ -> host |> box);
    do! next.Invoke(ctx)
    do! host.RunAsync();
  })

  [<EntryPoint>]
  let main args =
    let rc = NLoopServerCommandLine.getRootCommand()
    CommandLineBuilder(rc)
      .UseDefaults()
      .UseMiddleware(useWebHostMiddleware)
      .Build()
      .Invoke(args)
