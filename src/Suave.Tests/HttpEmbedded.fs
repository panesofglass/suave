﻿module HttpEmbedded

open Fuchu

open Suave
open Suave.Successful
open Suave.Embedded

open Suave.Tests.TestUtilities
open Suave.Testing

[<Tests>]
let embedded_resources cfg =
  let runWithConfig = runWith cfg

  testList "test Embedded.browse" [
      testCase "200 OK returns embedded file" <| fun _ ->
        Assert.Equal("expecting 'Hello World!'", "Hello World!", runWithConfig browseDefaultAsssembly |> req HttpMethod.GET "/embedded-resource.txt" None)
    ]