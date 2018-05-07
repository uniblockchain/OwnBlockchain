﻿namespace Chainium.Blockchain.Public.Node

open System
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Chainium.Blockchain.Public.Core.DomainTypes
open Chainium.Blockchain.Public.Core.Dtos

module Api =

    let toApiResponse mapData = function
        | Ok data ->
            data
            |> mapData
            |> json
        | Error errors ->
            errors
            |> List.map (fun (AppError e) -> e)
            |> (fun es -> { ErrorResponseDto.Errors = es })
            |> json


    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Handlers
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let submitTxHandler : HttpHandler = fun next ctx ->
        task {
            let! requestDto = ctx.BindJsonAsync<SubmitTxRequestDto>()

            let response =
                Composition.submitTx requestDto
                |> toApiResponse (fun (TxHash txHash) -> { SubmitTxResponseDto.TxHash = txHash })

            return! response next ctx
        }


    ////////////////////////////////////////////////////////////////////////////////////////////////////
    // Configuration
    ////////////////////////////////////////////////////////////////////////////////////////////////////

    let api =
        choose [
            GET >=> choose [
                route "/" >=> text "TODO: Show link to the help page"
            ]
            POST >=> choose [
                route "/tx" >=> submitTxHandler
            ]
        ]

    let configureApp (app : IApplicationBuilder) =
        // Add Giraffe to the ASP.NET Core pipeline
        app.UseGiraffe api

    let configureServices (services : IServiceCollection) =
        // Add Giraffe dependencies
        services.AddGiraffe() |> ignore

    let start () =
        WebHostBuilder()
            .UseKestrel()
            .Configure(Action<IApplicationBuilder> configureApp)
            .ConfigureServices(configureServices)
            .Build()
            .Run()
