# Search offline

Lexical search never needs the network. Semantic and hybrid search need the model, which downloads on first use. To run them offline:

1. While online, fetch the model:

   ```sh
   ferret --model download
   ```

   Library: call `FerretClient.EnsureModel()`.

2. On the offline machine, stop Ferret trying to download:

   ```sh
   export FERRET_OFFLINE=1
   ```

   A missing model is now an error that names the directory it looked in, instead of a download attempt.

## Use a model you already have

Put `model_quantized.onnx` and `vocab.txt` in a directory and point Ferret at it:

```sh
export FERRET_MODEL_DIR=/path/to/model
```

Library: set `FerretOptions.ModelDir`.

## Use the bundled build

A build made with `-p:BundleModel=true` embeds the model and needs no download. Each GitHub release has one per platform, named `ferret-bundled-<rid>`. With mise, use `mise use -g 'github:dburriss/ferret[matching=bundled]'`.

See [Environment variables and files](../reference/configuration.md) for the lookup order.
