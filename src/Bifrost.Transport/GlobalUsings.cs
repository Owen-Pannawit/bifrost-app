// Android.App.Result (an activity result code) collides with Bifrost.Core.Model.Result.
// Alias it once for the whole project rather than per-file — every transport hits this.
// Result<T> is unambiguous: Android has no generic Result.
global using Result = Bifrost.Core.Model.Result;
