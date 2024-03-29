namespace NLoop.Server

open System
open System.CommandLine.Builder
open System.CommandLine.Invocation
open System.CommandLine.Hosting
open System.CommandLine.Parsing
open System.IO
open System.Net
open System.Runtime.CompilerServices
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting.Internal
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

open NLoop.Domain
open NLoop.Domain.IO
open NLoop.Server
open NLoop.Server.DTOs
open NLoop.Server.Pipelines
open NLoop.Server.RPCDTOs
open NLoop.Server.Services

open FSharp.Control.Tasks.Affine
open NReco.Logging.File
open DotNetLightning.ClnRpc.Plugin
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
      subRoute "/v1" (choose [
        GET >=>
          route "/info" >=> QueryPipelines.getInfoPipeline
          route "/version" >=> json Constants.AssemblyVersion
        subRoute "/loop" (choose [
          POST >=>
            route "/out" >=> mustAuthenticate >=> bindJson<LoopOutRequest> LoopPipelines.loopOutPipeline
            route "/in" >=> mustAuthenticate >=> bindJson<LoopInRequest> LoopPipelines.loopInPipeline
        ])
        subRoute "/swaps" (choose [
          GET >=>
            route "/history" >=> QueryPipelines.getSwapHistoryPipeline
            route "/ongoing" >=> QueryPipelines.getOngoingSwapPipeline
            routef "/%s" (SwapId.SwapId >> QueryPipelines.getSwapPipeline)
        ])
        subRoute "/cost" (choose [
          GET >=>
            route "/summary" >=> QueryPipelines.getCostSummaryPipeline
        ])
        subRoute "/auto" (choose [
          GET >=>
            route "/suggest" >=> (AutoLoopPipelines.suggestSwapsPipeline None)
            routef "/suggest/%s" (SupportedCryptoCode.TryParse >> AutoLoopPipelines.suggestSwapsPipeline)
        ])
        subRoute "/liquidity" (choose [
          route "/params" >=> choose [
            POST >=> bindJson<SetLiquidityParametersRequest> (AutoLoopPipelines.setLiquidityParamsPipeline None)
            GET >=> AutoLoopPipelines.getLiquidityParamsPipeline SupportedCryptoCode.BTC
          ]
          GET >=> routef "/params/%s" (fun s ->
            s.ToUpperInvariant()
            |> SupportedCryptoCode.Parse
            |> AutoLoopPipelines.getLiquidityParamsPipeline
          )
          POST >=> routef "/params/%s" (fun offChain ->
            offChain.ToUpperInvariant()
            |> SupportedCryptoCode.Parse
            |> Some
            |> AutoLoopPipelines.setLiquidityParamsPipeline
            |> bindJson<SetLiquidityParametersRequest>
          )
        ])
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
    do
      (match env.IsDevelopment() with
      | true  ->
        app
          .UseDeveloperExceptionPage()
          .UseMiddleware<RequestResponseLoggingMiddleware>()
          |> ignore
      | false -> ())
    app
      .UseGiraffeErrorHandler(errorHandler)
      .UseCors(configureCors opts) |> ignore
    app
      .UseAuthentication()
      .UseGiraffe(webApp)

  let configureServices coldStart (env: IHostEnvironment option) (services : IServiceCollection) =
      // json settings
      let jsonOptions = JsonSerializerOptions()
      jsonOptions.AddNLoopJsonConverters()
      services
        .AddSingleton(jsonOptions)
        .AddSingleton<Json.ISerializer>(SystemTextJson.Serializer(jsonOptions)) |> ignore // for giraffe

      services.AddNLoopServices(coldStart)

      if (env.IsSome && env.Value.IsDevelopment()) then
        services.AddTransient<RequestResponseLoggingMiddleware>() |> ignore
        services.AddSingleton<RecyclableMemoryStreamManager>() |> ignore

      services.AddCors()    |> ignore

      services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCertificate(fun o -> o.AllowedCertificateTypes <- CertificateTypes.SelfSigned)
        .AddCertificateCache()
        .AddCookie(fun _o -> ())
        |> ignore

      services.AddGiraffe() |> ignore

  let configureServicesWithColdStart services = configureServices true None services

type Startup(_conf: IConfiguration, env: IHostEnvironment) =
  member this.Configure(appBuilder) =
    App.configureApp(appBuilder)

  member this.ConfigureServices(services) =
    App.configureServices false (Some env) services


[<AutoOpen>]
module Configurations =
  let configureFileLogging (ctx: HostBuilderContext) (builder : ILoggingBuilder) =
      let isProduction = ctx.HostingEnvironment.IsProduction()
      if isProduction |> not then
        builder.AddDebug() |> ignore
      let configureFileLogging (opts: FileLoggerOptions) =
        opts.Append <- isProduction
        opts.MinLevel <- if isProduction then LogLevel.Debug else LogLevel.Trace
        ()
      let opts =
        let sp = builder.Services.BuildServiceProvider()
        sp.GetRequiredService<IOptions<NLoopOptions>>()

      let filePath =
        let date = DateTimeOffset.Now.ToString("yyyy-MM-dd")
        let logFile = $"{date}-nloop.log"
        Path.Combine(opts.Value.DataDirNetwork, logFile)
      builder
        .AddConfiguration(ctx.Configuration.GetSection("Logging"))
        .AddFile(filePath, configureFileLogging)
        .SetMinimumLevel(LogLevel.Debug)
        |> ignore

  let configureLogging(ctx: HostBuilderContext) (builder: ILoggingBuilder) =
      builder.AddConsole() |> ignore
      configureFileLogging ctx builder

  let configureJsonRpcLogging (outputStream: Stream) (ctx: HostBuilderContext) (builder: ILoggingBuilder) =
    builder.AddPluginLogger(outputStream) |> ignore
    configureFileLogging ctx builder

  let configureConfig (ctx: HostBuilderContext)  (builder: IConfigurationBuilder) =
    builder.AddInMemoryCollection(Constants.DefaultLoggingSettings) |> ignore
    builder.SetBasePath(Directory.GetCurrentDirectory()) |> ignore
    Directory.CreateDirectory(Constants.HomeDirectoryPath) |> ignore
    let iniFile = Path.Join(Constants.HomeDirectoryPath, "nloop.conf")
    if (iniFile |> File.Exists) then
      builder.AddIniFile(iniFile) |> ignore
    let env = ctx.HostingEnvironment
    builder
      .AddJsonFile("appsettings.json", optional = true)
      .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional=true)
      .AddEnvironmentVariables(prefix="NLOOP_") |> ignore
    ()

[<AbstractClass;Sealed;Extension>]
type ConfigurationExtension =

  [<Extension>]
  static member ConfigureAsPlugin(hostBuilder: IHostBuilder, outputStream, ?coldStart) =
    let coldStart = defaultArg coldStart false
    hostBuilder
      .ConfigureLogging(configureJsonRpcLogging outputStream)
      .ConfigureServices(App.configureServices coldStart None)
      .ConfigureServices(fun serviceCollection ->
        serviceCollection
          .AddSingleton<ILogger, Logger<ConfigurationExtension>>() // for startup logging
          .AddSingleton<NLoopJsonRpcServer>()
          .AddSingleton<NLoopOptionsHolder>()
          .AddSingleton<ILightningClientProvider, ClnLightningClientProvider>()
          |> ignore
    )

  [<Extension>]
  static member ConfigureAsWebServer(hostBuilder: IHostBuilder) =
    hostBuilder
      .ConfigureLogging(configureLogging)
      .ConfigureWebHostDefaults(
        fun webHostBuilder ->
          webHostBuilder
            .UseStartup<Startup>()
            .UseUrls()
            .UseKestrel(fun kestrelOpts ->
              let opts = kestrelOpts.ApplicationServices.GetRequiredService<IOptions<NLoopOptions>>().Value
              let logger = kestrelOpts.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger<Startup>()

              logger.LogInformation $"Starting {Constants.AppName}. version: {Constants.AssemblyVersion}"

              let ipAddresses = ResizeArray<_>()
              match opts.RPCHost |> IPAddress.TryParse with
              | true, ip ->
                ipAddresses.Add(ip)
              | false, _ when opts.RPCHost = Constants.DefaultRPCHost ->
                ipAddresses.Add(IPAddress.IPv6Loopback)
                ipAddresses.Add(IPAddress.Loopback)
              | _ ->
                ipAddresses.Add(IPAddress.IPv6Any)

              if opts.NoHttps then
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
            |> ignore
      )

module Main =

  let configureHostBuilder (outputStream: Stream) (hostBuilder: IHostBuilder) =
    let hostBuilder = hostBuilder.ConfigureAppConfiguration(configureConfig)
    let isPluginMode = Environment.GetEnvironmentVariable("LIGHTNINGD_PLUGIN") = "1"
    if isPluginMode then
      hostBuilder.ConfigureAsPlugin(outputStream)
    else
      hostBuilder.ConfigureAsWebServer()

  /// Mostly the same with `CommandLineBuilder.UseHost`, but it will call `IHost.RunAsync` instead of `StartAsync`,
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
    ) |> ignore
    let isPluginMode = Environment.GetEnvironmentVariable("LIGHTNINGD_PLUGIN") = "1"
    hostBuilder
      .UseInvocationLifetime(ctx, fun o -> if isPluginMode then o.SuppressStatusMessages <- true)
      // override `InvocationLifetime` which does not handle SIGTERM/SIGINT.
      .UseConsoleLifetime(fun o -> if isPluginMode then o.SuppressStatusMessages <- true) 
      |> ignore
      
    let o =
      Console.OpenStandardOutput()
      |> Stream.Synchronized
    configureHostBuilder o hostBuilder |> ignore

    use host = hostBuilder.Build();
    ctx.BindingContext.AddService(typeof<IHost>, fun _ -> host |> box);
    do! next.Invoke(ctx)

    if isPluginMode then
      use logScope = host.Services.CreateScope()
      let logger = logScope.ServiceProvider.GetRequiredService<ILogger>()
        
      let server = host.Services.GetRequiredService<NLoopJsonRpcServer>()
      let! _ =
        let i = Console.OpenStandardInput()
        server.StartAsync(o, i, CancellationToken.None)
      if server.InitializationStatus = PluginInitializationStatus.Failed then
        ()
      if (server.InitializationStatus = PluginInitializationStatus.InitializedSuccessfully) then
        logger.LogInformation "Plugin Started"
        do! host.RunAsync()
    else
      do! host.RunAsync()
  })

  [<EntryPoint>]
  let main args =
    let rc = NLoopServerCommandLine.getRootCommand()
    CommandLineBuilder(rc)
      .UseDefaults()
      .UseMiddleware(useWebHostMiddleware)
      .Build()
      .Invoke(args)
