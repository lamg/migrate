[<MigLib.Dsl.Attributes.GeneratedDbNamespace("ExampleApp")>]
module ExampleMigSchema.StudentExtensions

type StudentOpt = WithAddress of MigSchema.Student * address: string
