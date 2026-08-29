# Building the ATAS side

This project is **excluded from the solution build** because it needs assemblies
that only exist on a machine with ATAS installed. That is intentional — it keeps
`dotnet build` and `dotnet test` working everywhere else, including CI.

## To build it on your ATAS machine

1. Find your ATAS install directory (usually `C:\Program Files (x86)\ATAS Platform`).
2. Create `src/Bridge.Atas/Bridge.Atas.csproj` from the template below, fixing
   `<AtasPath>` and `<TargetFramework>` to match your install.
3. Check your ATAS version under **Help → About** and confirm the `[ATAS-API]`
   marked lines in `BridgeStrategy.cs` compile. If a member name has moved, the
   compiler will point straight at it — the surrounding logic is unaffected.
4. Copy the built DLL into `%USERPROFILE%\Documents\ATAS\Strategies\`.
5. Restart ATAS, add **Bridge Link** to a chart, set the port, start it.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- net472 for older ATAS, net6.0-windows / net8.0-windows for 2023+ builds -->
    <TargetFramework>net8.0-windows</TargetFramework>
    <AtasPath>C:\Program Files (x86)\ATAS Platform</AtasPath>
    <LangVersion>latest</LangVersion>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="ATAS.Indicators">
      <HintPath>$(AtasPath)\ATAS.Indicators.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="ATAS.Strategies">
      <HintPath>$(AtasPath)\ATAS.Strategies.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="OFT.Attributes">
      <HintPath>$(AtasPath)\OFT.Attributes.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

`<Private>false</Private>` matters: it stops your DLL shipping copies of ATAS's
own assemblies, which is a common cause of load conflicts inside the platform.

## Note on dependencies

`WireCodec.cs` is dependency-free on purpose so you can paste it into this
project rather than referencing `Bridge.Wire`. A plugin loaded into the ATAS
process that drags in its own NuGet packages risks version-conflicting with
whatever ATAS itself ships — a failure that only ever appears on the live
machine.
