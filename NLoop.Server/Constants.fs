namespace NLoop.Server

open System
open System.IO

[<RequireQualifiedAccess>]
module Constants =
  let HomePath =
     let envHome = if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then "HOMEPATH" else "HOME"
     Environment.GetEnvironmentVariable(envHome)

  [<Literal>]
  let HomeDirectoryName = ".nloop"
  let HomeDirectoryPath = Path.Join(HomePath, HomeDirectoryName)
  let DefaultDataDirectoryPath = Path.Join(HomeDirectoryPath, "data")

  [<Literal>]
  let DefaultNoHttps = false
  [<Literal>]
  let DefaultHttpsPort = 443

  [<Literal>]
  let DefaultHttpsHost = "localhost"

  let DefaultHttpsCertFile = Path.Combine(HomePath, ".aspnet", "https", "ssl.cert")

  let DefaultCookieFile = Path.Combine(DefaultDataDirectoryPath, "cookie")

  [<Literal>]
  let DefaultRPCHost = "localhost"
  [<Literal>]
  let DefaultRPCPort = 5000

  let DefaultRPCAllowIp = [|"localhost"|]

  [<Literal>]
  let DefaultBoltzServer = "https://boltz.exchange/api"

  [<Literal>]
  let DefaultBoltzPort = 443

  [<Literal>]
  let DefaultBoltzHttps = true

  [<Literal>]
  let DefaultLightningConnectionString = "type=lnd-rest;server=http://localhost:8080;allowinsecure=true"

  type private Foo = Bar
  let AssemblyVersion =
    Bar.GetType().Assembly.GetName().Version.ToString()
