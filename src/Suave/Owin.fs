module Suave.Owin

// Following the specification:
// https://github.com/owin/owin/blob/master/spec/owin-1.0.1.md

open System
open System.Net
open System.Threading
open System.Collections.Generic
open System.Threading.Tasks

open Suave
open Suave.Http
open Suave.Types
open Suave.Sockets

open Freya.Core
open Freya.Core.Integration

type OwinApp =
  OwinEnvironment -> Async<unit>

[<RequireQualifiedAccess>]
[<CompilationRepresentation (CompilationRepresentationFlags.ModuleSuffix)>]
module OwinAppFunc =

  open Aether
  open Aether.Operators
  open Suave.Utils
  open Suave.Sockets.Control
  open Suave.Web.ParsingAndControl

  type OwinRequest =
    abstract OnSendingHeadersAction : Action<Action<obj>, obj>

  let isoBox<'t> : Iso<'t, obj> =
    box, unbox

  type internal DWr(initialState) =
    let state : HttpContext ref = ref initialState

    let req l = HttpContext.request_ >--> l <--> isoBox
    let run l = HttpContext.runtime_ >--> l <--> isoBox
    let uriAbsolutePath : Property<_, _> =
      (fun (uri : Uri) -> uri.AbsolutePath),
      (fun v uri -> UriBuilder(uri, Path = v).Uri)
    let requestHeaders : Property<_, _> =
      (fun headers -> Map.ofList headers :> IDictionary<string,string>),
      (fun v _ -> [ for KeyValue(header, value) in v -> header, value ] )
    let requestId : Property<_, _> =
      (fun (trace: Logging.TraceHeader) -> trace.reqId),
      (fun v trace -> { trace with reqId = v })
    let requestUser : Property<_, _> =
      (fun (userState : Map<string, obj>) ->
        userState |> Map.tryFind "user" |> function | Some x -> box x | None -> box null),
      (fun v userState ->
        let userState' = if userState.ContainsKey("user") then userState.Remove("user") else userState
        userState'.Add("user", v))

    let owinMap =
      [ (* 3.2.1 Request Data *)
        Constants.requestScheme, req HttpRequest.httpVersion_
        Constants.requestMethod, req HttpRequest.method_
        Constants.requestPathBase, run HttpRuntime.homeDirectory_
        Constants.requestPath, req (HttpRequest.url_ >--> uriAbsolutePath)

        Constants.requestQueryString, req HttpRequest.rawQuery_
        Constants.requestProtocol, req HttpRequest.httpVersion_
        Constants.requestHeaders, req (HttpRequest.headers_ >--> requestHeaders)
        Constants.requestBody, req HttpRequest.rawForm_
        Constants.requestId, req (HttpRequest.trace_ >--> requestId)
        Constants.requestUser, HttpContext.user_state_ >--> requestUser <--> boxIso

        (* 3.2.2 Response Data *)
        //Constants.responseStatusCode, // etc, wrap in the lenses
        //Constants.responseReasonPhrase
        //Constants.responseProtocol
        //Constants.responseHeaders
        //Constants.responseBody

        (* 3.2.3 Other Data *)
        //Constants.callCancelled
        //Constants.owinVersion
      ]

    let owinKeys = owinMap |> List.map fst |> Set.ofSeq
    let owinRW   = owinMap |> Map.ofList

    member x.Interface =
      x :> IDictionary<string, obj>

    member x.State =
      state

    interface OwinRequest with
      member x.OnSendingHeadersAction =
        Action<_, _>(fun a -> fun x -> ())

    interface IDictionary<string, obj> with

      member x.Add (k, v) =
        // when you 'add' a key, you have to change the state for that key, in
        // Aether notation:
        // set the state to the
        state := Lens.set (owinRW |> Map.find k) v !state

      member x.Remove k =
        // does it get removed before added?
        // in that case you need to keep track of the last value in the container
        // of the value you're removing, so that it may be updated with the lens
        // on Add
        ()

      member x.ContainsKey k =
        // we have ALL THE KEYS!!!
        owinKeys.Contains k

  let private wrap (ctx : HttpContext) =
    DWr ctx

  [<CompiledName "ToSuave">]
  let ofOwin (owin : OwinApp) : WebPart =
    fun (ctx : HttpContext) ->
      let impl conn : SocketOp<unit> = socket {
        let wrapper = wrap ctx
        do! SocketOp.ofAsync (owin wrapper.Interface)
        let ctx = !wrapper.State
        // if wrapper has OnHeadersSend, call that now => a new HttpContext (possibly), assign to ctx
        do! Web.ParsingAndControl.writePreamble ctx
        do! Web.ParsingAndControl.writeContent ctx ctx.response.content
      }

      { ctx with
          response =
            { ctx.response with
                content = SocketTask impl
                writePreamble = false
            }
      }
      |> succeed

  let ofOwinFunc (owin : OwinAppFunc) =
    ofOwin (fun e -> Async.AwaitTask ((owin.Invoke e).ContinueWith<_>(fun _ -> ())))

module OwinServerFactory =

  type Dic<'Key, 'Value> = IDictionary<'Key, 'Value>

  let private read (d : OwinEnvironment) k f =
    match d.TryGetValue k with
    | false, _ -> f ()
    | true, value -> value :?> 'a

  let private ``read_env!`` (e : OwinEnvironment) key =
    let res = read e key (fun () -> new Dictionary<string, obj>() :> OwinEnvironment)
    e.[key] <- res
    res

  let private ``read_dic!`` (e : OwinEnvironment) key =
    let res = read e key (fun () -> new Dictionary<string, 'a>())
    e.[key] <- res
    res

  let private ``read_list!`` (e : OwinEnvironment) key =
    let res = read e key (fun () -> new List<'a>() :> IList<'a>)
    e.[key] <- res
    res

  let private get (e : OwinEnvironment) key =
    match e.TryGetValue key with
    | false, _ -> failwithf "missing value for key '%s'" key
    | true, value -> value :?> 'a

  let private get_default (e : OwinEnvironment) key map defaults =
    match e.TryGetValue key with
    | false, _ -> defaults
    | true, value -> map (value :?> 'a)

  [<CompiledName "Initialize">]
  let initialize (props : Dic<string, obj>) =
    if props = null then nullArg "props"
    props.[Constants.owinVersion] <- "1.0.1"
    let cap = ``read_env!`` props Constants.CommonKeys.capabilities
    cap.[Constants.CommonKeys.serverName] <- Globals.Internals.server_name

  [<CompiledName ("Create")>]
  let create (app : OwinAppFunc, props : Dic<string, obj>) =
    if app = null then nullArg "app"
    if props = null then nullArg "props"

    let bindings =
      (``read_list!`` props Constants.CommonKeys.addresses
       : IList<OwinEnvironment>)
      |> Seq.map (fun dic ->
        let port   = get dic "port" : string
        let ip     = read dic "ip" (fun () -> "127.0.0.1") : string
        let scheme = get_default dic "certificate" (fun _ -> HTTP) HTTP
        { scheme = scheme; socketBinding = { ip = IPAddress.Parse ip; port = uint16 port } })
      |> List.ofSeq

    let serverCts = new CancellationTokenSource()

    let conf =
      { Web.defaultConfig with
          bindings          = bindings
          cancellationToken = serverCts.Token }

    let started, listening =
      Web.startWebServerAsync conf (OwinAppFunc.ofOwinFunc app)

    let _ = started |> Async.RunSynchronously

    { new IDisposable with
      member x.Dispose () =
        // note: this won't let the web requests finish gently
        serverCts.Cancel()
        serverCts.Dispose()
      }
