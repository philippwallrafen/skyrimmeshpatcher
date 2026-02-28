# README

using .NET 8

run with 'dotnet run' - will patch all files in input and save to output

additional options are:  
--in  
--out  
--verify  
--copy-unchanged  
--open-only # only patches opening animations, useful for containers which should open instant but can close slowly  
--force-shared # only for shared keys when using arg --open-only

e.g. 'dotnet run -- --open-only --verify --force-shared' for containers
