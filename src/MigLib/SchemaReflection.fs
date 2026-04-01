module Mig.SchemaReflection

let internal toSnakeCase = SchemaReflectionNaming.toSnakeCase
let internal buildSchemaFromTypes = SchemaReflectionAssembly.buildSchemaFromTypes

let internal buildSchemaFromAssembly =
  SchemaReflectionAssembly.buildSchemaFromAssembly

let internal buildSchemaFromAssemblyModule =
  SchemaReflectionSeed.buildSchemaFromAssemblyModule
