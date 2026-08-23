// Android.App.Result (an activity result code) collides with Bifrost.Core.Model.Result.
// Alias once for the project rather than per-file. Result<T> is unambiguous — Android has no
// generic Result.
global using Result = Bifrost.Core.Model.Result;
