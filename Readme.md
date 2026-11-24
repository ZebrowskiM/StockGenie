
# CoinGlimpse

## What is it?

Stock Genie is an api wrapper around coingecko demo API that can retrieve, track and analyze prices of crpyto currencies. Offering TA and Analyze tools to allow for integration with your front ends.

## Build Instructions

Configure the port number and build the `CoinGlimpse.API` project

## Setting Coingecko API Key

Replace the API key and run this in CLI

``` bash
dotnet user-secrets init --project .\CoinGlimpse.API
dotnet user-secrets set "CoinGecko:ApiKey" "YOUR_KEY" --project .\CoinGlimpse.API 
```

## License and citation

The original project code and documentation are licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for attribution. Third-party dependencies and CoinGecko data are not covered by this grant and remain subject to their own terms.

If you reference this project in a paper, post, or other work, please cite [Marek Zebrowski's CoinGlimpse](https://github.com/ZebrowskiM/StockGenie). GitHub's **Cite this repository** button uses the metadata in [CITATION.cff](CITATION.cff). If reproducibility matters, include the commit you used.
