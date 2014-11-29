/// The session module has two components; the stateless 'authed' component
/// which sets a http-only cookie and verifies its signature/hmac matches that
/// which is generated by the server, and another component, the stateless,
/// which also maintains a stateful Map<string, obj> of data for the user's
/// session id.
module Suave.Session

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open System.Runtime.Caching
open Types
open Http

[<Literal>]
let SessionAuthCookie = "auth"

/// This is the cookie that the client-side can use to check whether the
/// user is logged on or not. It's only a marker, which is used for this
/// purpose; the real auth cookie is the SessionHttpCookie, which isn't
/// available to client-side.
[<Literal>]
let SessionIdCookie = "sid"

module internal Utils =
  /// This is used to pack base64 in a cookie; generates a degenerated base64 string
  let base64_headers bytes =
    let base64 = Convert.ToBase64String bytes
    base64.Replace('+','-').Replace('/','_').Trim([| '=' |])

/// Use to set a session-id for the client, which is a way is how the client
/// is 'authenticated'.
module Auth =

  [<Literal>]
  let ServerKeyLength = 64

  [<Literal>]
  let SessionIdLength = 40

  /// Extracts the actual session id and the mac value from the cookie's data.
  let private parse_cookie_data (cd : string) =
    if cd.Length <= SessionIdLength then
      None
    else
      let id  = cd.Substring(0, SessionIdLength)
      let mac = cd.Substring(SessionIdLength)
      Some (id, mac)

  /// Returns a list of the hmac data to use, from the request.
  let private hmac_data session_id (request : HttpRequest) =
    [ session_id
      request.ipaddr.ToString()
      request.headers %% "user-agent" |> Option.or_default ""
    ]

  /// Set +relative_expiry time span on the expiry time of the cookies
  let private sliding_expiry relative_expiry auth_cookie client_cookie =
    let expiry = Globals.utc_now().Add relative_expiry
    { auth_cookie   with expires = Some expiry },
    { client_cookie with expires = Some expiry }

  /// The key used in `context.user_state` to save the session id for downstream
  /// web parts.
  [<Literal>]
  let SessionAuth = "Suave.Session.Auth"

  /// Create a new client cookie with the session id in.
  let private mk_client_cookie session_id =
    { HttpCookie.mk' SessionIdCookie session_id with http_only = false }

  /// Generate one server auth-side cookie and one client-side cookie.
  let generate_cookies relative_expiry secure { request = req; runtime = run } =
    let session_id  = Crypto.generate_key' SessionIdLength
    let hmac_data   = hmac_data session_id req
    let hmac        = Crypto.hmac' run.server_key hmac_data |> Utils.base64_headers
    let cookie_data = String.Concat [| session_id; hmac |]
    let auth_cookie, client_cookie =
      sliding_expiry relative_expiry
        { HttpCookie.mk' SessionAuthCookie cookie_data with http_only = true; secure = secure }
        (mk_client_cookie session_id)
    auth_cookie, client_cookie, session_id

  /// Unsets the cookies, thereby unauthenticating the user.
  let unset_cookies (auth_cookie, client_cookie, session_id) : WebPart =
    Writers.unset_cookie auth_cookie >>=
      Writers.unset_cookie client_cookie >>=
      Writers.unset_user_data session_id

  /// Sets the http-cookie, the client-cookie and sets the value of SessionAuth
  /// in the user_data of the HttpContext.
  let set_cookies (auth_cookie, client_cookie, session_id) : WebPart =
    Writers.set_cookie auth_cookie >>=
      Writers.set_cookie client_cookie >>=
      Writers.set_user_data SessionAuth session_id

  /// Bumps the expiry dates for all the cookies and user_data value.
  let refresh_cookies relative_expiry auth_cookie session_id : WebPart =
    let a', c' = sliding_expiry relative_expiry auth_cookie (mk_client_cookie session_id)
    set_cookies (a', c', session_id)

  /// Validates the inner cookie data (use validate' for simplicity) and
  /// returns true if the calculated HMAC matches the given HMAC.
  let validate server_key request session_id hmac_given =
    let hmac_calc =
      hmac_data session_id request
      |> Crypto.hmac' server_key
      |> Utils.base64_headers
    String.cnst_time_cmp_ord hmac_given hmac_calc

  /// Validates the one server-side cookie that can be generated with 'generate_cookies',
  /// and returns the HttpCookie, session id string and given hmac value if it's all
  /// valid.
  let validate' ({runtime = { server_key = key }; request = req } as ctx : HttpContext) =
    let cookies =
      ctx
      |> HttpContext.request
      |> HttpRequest.cookies
    cookies
    |> Map.tryFind SessionAuthCookie
    |> Option.bind (fun auth_cookie ->
        parse_cookie_data auth_cookie.value
        |> Option.map (fun x -> auth_cookie, x))
    |> Option.bind (fun (auth_cookie, (session_id, hmac_given)) ->
        if validate key req session_id hmac_given then
          Some (auth_cookie, session_id, hmac_given)
        else None)

  let authenticate (relative_expiry : TimeSpan) (failure : WebPart) : WebPart =
    context (fun ctx ->
      match validate' ctx with
      | Some (auth_cookie, session_id, _) ->  
        refresh_cookies relative_expiry auth_cookie session_id
      | None   -> failure)

  /// Ensure the client/http context has a valid session id which fails by
  /// failing the full web part; but you can use the non-primed method if
  /// you'd rather give a UNAUTHORIZED reply. It's a good idea not to leak
  /// information about secred entities by not showing there's something
  /// that the user wasn't authorized to view.
  let authenticate' relative_expiry : WebPart =
    authenticate relative_expiry never

  /// Set server-signed cookies to make the response contain a cookie
  /// with a valid session id. It's worth having in mind that when you use this web
  /// part, you're setting cookies on the response; so you'll need to have the
  /// client re-send a request if you require authentication for it, after this
  /// web part has run.
  ///
  /// Parameters:
  ///  - `relative_expiry`: how long does the authentication cookie last?
  /// - `secure`: HttpsOnly?
  let authenticated (relative_expiry : TimeSpan) secure : WebPart =
    context (fun ctx ->
      choose [
        // either we're already authenticated and then we just refresh cookies
        // or otherwise we set fresh cookies
        authenticate relative_expiry never
        set_cookies (generate_cookies relative_expiry secure ctx) ])

  [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
  module HttpContext =
    let session_id x =
      x.user_state
      |> Map.tryFind SessionAuth
      |> Option.map (fun x -> x :?> string)

/// Common for this module is that it requires that the Auth module
/// above has been activated/used and that the user is authenticated.
module State =
  open Auth

  /// Anything stateful implies the user is 'authenticated'. What it means for
  /// your application that the user is authenticated in the sense that Suave
  /// means it, is up to you as a programmer. You can choose to let all users
  /// be authenticated with Suave.Session.Auth and then use this session store
  /// with the session ids that that generates.
  ///
  /// A wilful refactor of this module would allow you to separate different
  /// *sorts* of authentication.
  let stateful relative_expiry
               failure
               (state_store_type : string)
               (session_store : (*session id*) string -> StateStore)
               : WebPart =
    authenticate relative_expiry failure >>=
      context (fun ctx ->
        match ctx.user_state |> Map.tryFind state_store_type with
        | None       ->
          let session_id = ctx |> HttpContext.session_id |> Option.get
          Writers.set_user_data state_store_type (session_store session_id)
        | Some store ->
          succeed)

  [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
  module CookieStateStore =
    ()

  /// This module contains the implementation for the memory-cache backed session
  /// state store, when the memory cache is global for the server.
  [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
  module MemoryCacheStateStore =

    /// This key will be present in HttpContext.user_state and will contain the
    /// MemoryCache instance.
    [<Literal>]
    let StateStoreType = "Suave.Session.State.MemoryCacheStateStore"

    let private wrap (session_map : MemoryCache) relative_expiry session_id =
      let state_bag =
        lock session_map (fun _->
          if session_map.Contains session_id then
            session_map.Get session_id
            :?> ConcurrentDictionary<string, obj>
          else
            let cd = new ConcurrentDictionary<string, obj>()
            let policy = CacheItemPolicy(SlidingExpiration = relative_expiry)
            session_map.Set(CacheItem(session_id, cd), policy)
            cd)

      { new StateStore with
          member x.get key       =
            if state_bag.ContainsKey key then
              Some (state_bag.[key] :?> 'a)
            else None
          member x.set key value =
            state_bag.[key] <- value }

    let stateful failure (relative_expiry : TimeSpan) =
      stateful relative_expiry
               failure
               StateStoreType
               (wrap (MemoryCache.Default) relative_expiry)

    let DefaultExpiry = TimeSpan.FromMinutes 30.
    
    let stateful' : WebPart =
      stateful never DefaultExpiry

    /// Extensions to HttpContext for Session support.
    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module HttpContext =

      /// Read the session store from the HttpContext, or throw an exception otherwise.
      /// If this throws an exception, it's a programming error from the consumer of the
      /// suave library.
      let state (ctx : HttpContext) : StateStore =
        ctx.user_state
        |> Map.tryFind StateStoreType
        |> Option.map (fun ss -> ss :?> StateStore)
        |> Option.get
