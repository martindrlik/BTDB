# Changelog

## [unreleased]

## 20.2.0

### Added

- IOC now support `Dependency` attribute for properties injection. Also it could be used for renaming dependency resolved name. Nullable reference types are optional dependencies.

## 20.1.0

### Added

- IOC now supports public properties injection. Registration needs to be done with `PropertiesAutowired()`. Setters does not need to be public. Nullable reference types are optional dependencies, all other properties are required.

## 20.0.0

### Added

- Added support for `IOrderedSet<T>` lazily stored set.
- EventLayers deserialization can now unwrap `IIndirect<T>`, making it compatible change (`IIndirect<T>` => `T`, or `IDictionary<TKey, IIndirect<T>>` => `IDictionary<TKey, T>`).
- New documentation for [supported types](Doc/SupportedTypes.md)
- Added support for `ISet<T>`, `HashSet<T>` with identical serialization as `IList<T>`.
- Removed some allocations from `IOrderedDictionary`

## 19.9.3

### Fixed

- Regression with DB loading `IDictionary<Key,IIndirect<SomeAbstractClass>>`

## 19.9.2

## 19.9.1

### Fixed

- Regression from 19.8.0 with NullReferenceException in some special cases.

## 19.9.0

### Added

- Suffixes for partial deserializations in methods (FindBy,ListBy) does not need to be separated by underscore anymore.

## 19.8.0

### Added

- Relations now support returning only partial classes. For example it allows to speed up table scanning because you can deserialize only fields you need when enumerating relation.

## 19.7.1

### Fixed

- Made AesGcmSymmetricCipher thread safe.

## 19.7.0

### Added

- Support `EncryptedString` in DB indexes (orderable).

## 19.6.0

### Added

- New `EncryptedString` type to be able to store string in its encrypted form. You need to pass `ISymmetricCipher` implementation to `DBOptions`, `TypeSerializersOptions`, `EventSerializer` and `EventDeserializer`. There is class `AesGcmSymmetricCipher` implementing `ISymmetricCipher`, which provides perfect security by just passing 32 bytes key to its constructor.

## 19.5.0

- Added possibility to deserialize event with Nullable to dynamic (usable for dumping EventStore)

## 19.4.0

### Added

- All serializations DB, Event now supports `System.Version` type. Default conversion allows to upgrade from `string` to `Version`. When `Version` is used in ordering, keys it behaves as expected.

### Fixed

Removed control flow by exceptions from `EnumerateSingletonTypes`. Fixes #85.

## 19.3.0

### Added

Relations new methods `AnyById` and `AnyBy{SecKeyName}` supported.

## 19.2.0

### Added

#### Relations

- New methods `CountById` and `CountBy{SecKeyName}` supported.
- `IEnumerator` and `IEnumerable` could be freely exchanged as result types.
- `ListById` and `ListBy{SecKeyName}` does not require `AdvancedEnumeratorParam`.

## 19.1.0

### Changed

Range defined by EndKey s KeyProposition.Included now contains all keys with passed prefix, used in methods: `RemoveById` `ListById` `ListBy{SecKeyName}`

## 19.0.0

### Breaking change

Needs to be compiled with in csproj:

    <LangVersion>8</LangVersion>
    <Nullable>annotations</Nullable>

`StructList.Add()` renamed to `AddRef()`.

### Added

IOC: IAsyncDisposable is not registered by AsImplementedInterfaces (same behavior as IDisposable).

## 18.2.2

### Fixed

Another bug in FindLast made me to delete it and rewrite again from managed heap implementation.

## 18.2.1

### Fixed

Bug in FindLast in native heap KVDB.

## 18.2.0

### Added

EventLayers Deserialization now supports classes without parameter-less constructor.

## 18.1.0

### Fixed

Find with prefix sometimes found records not matching prefix.

### Added

Improved CalcStats information.
Some small speed optimizations.

## 18.0.0

### Changed

Supports only .Net Core 3.0 or better.

### Added

New BTreeKeyValueDB implementation which uses native heap.

## 17.10.0

### Added

New method in relations: ShallowRemoveById
StartWritingTransaction returns ValueTask and optimized allocations.

## 17.9.0

### Added

ODBIterator extended to be able to seek and display only what is needed.

## 17.8.0

### Added

New method `IPlatformMethods.RealPath` for platform independent expanding of symlinks.

## 17.7.0

### Added

New method `ByteBuffer ByteBuffer.NewAsync(ReadOnlyMemory<byte> buffer)`.

FullNameTypeMapper improved support for generics. Types can migrate assemblies even for generic arguments. (by https://github.com/JanVargovsky)

### Changed

Supports only .Net Core 2.2 or better.

## 17.6.0

### Fixed

- When preserving history KVDB did not advising compaction without restarting application.

### Changed

- Default CompactorScheduler wait time to 30-45 minutes.

## 17.5.2

## 17.5.1

### Fixed

- Compactor does not ends in endless cycle when DB is opened with more than 4 times smaller split size than it was created.

## 17.5.0

### Added

ODbDump has new commands

- `leaks` which prints out unreachable objects in DB.
- `frequency` which prints number of items in relations and top level dictionaries in singletons

### Fixed

ODBLayer correctly supports interfaces in properties.

## 17.4.2

### Fixed

Additional nonderministic info removed from compare mode of ODbDump.

## 17.4.1

### Fixed

ODbDump is now published in way it works not just on my machine.

## 17.4.0

### Added

ODbDump is now part of release. ODbDump has new dump mode useful for comparing DBs.

## 17.3.0

RemoveById supports advanced enumeration param in relations

## 17.2.0

### Added

Extend TypeSerializers with optional configuration options.

Options consist of one option for `IIndirect<T>`, whether it is serialized or ignored.

## 17.1.0

### Added

Way to limit Compactor Write and Read Speed by setting `KeyValueDBOptions`. Default is unlimited.

## 17.0.1

### Changed

Added new method into IFileCollectionFile.AdvisePrefetch. It is called during DB open on files which are expected to be read by RandomRead.

## 17.0.0

### Changed

IFileCollection modified to allow faster implementations possible.

## 16.2.1

### Fixed

PRead behavior on Windows file end. Fixed Nested Dictionaries type gathering exception.

## 16.2.0

### Fixed

Failure to open DB in special case after erasing and compaction.
Type check when generating apart fields in relations.

## 16.1.0

### Added

Reintroduced PossitionLessStream and rename FileStream one to PossitionLessFileStream

## 16.0.0

### Improved

Much faster compaction when a lot of changes were done. New IKeyValueDB.CompactorRamLimitInMb does limit RAM usage for longer time.
Speed of OnDiskFileCollection improved by using new PRead and PWrite methods implemented for Windows and Posix.
Better exception in WriteInlineObject when object type could not be stored.

### Breaking Changes

Modified IKeyValueDBLogger and IKeyValueDB so implementation needs to be modified.

### Fixed

Skipping Events in EventStoreLayer Deserialization

## 15.1.0

### Added

Added way to skip Events in EventStoreLayer Deserialization.

### Fixed

Deletion of dictionaries during update/delete in relation in subclasses when not defined in declaration by interface.

## 15.0.0

### Added

ShallowUpsert and ShallowUpdate relation methods which does not try to prevent leaks, but are much faster.

### Changed

IIndirect objects are not automatically deleted during removal from relations.

## 14.12.2

### Fixed

Calling ListBy{SecondaryKey}OrDefault for not existing item during enumerating relations cooperates well.

## 14.12.1

### Fixed

Exception in EventStore2Layer serialization does not corrupt next serializations anymore.
Serialization of non Dictionary in EventStore does not fail.

## 14.12.0

### Fixed

Skipping removed field (inline object) when deserializing older version in relations

## 14.11.0

### Added

EventLayer serializers support IOrderedDictionary<K,V> type

## 14.10.0

### Added

ArtInMemoryKeyValueDB - less memory hungry KVDB - use it only in .NetCore 2.1 target
Generics classes now supported in EventLayer serializers
DBOptions.WithSelfHealing switches db to try self heal rather then fail fast mode
IObjectDBLogger for ObjectDB, actually for reporting deletion of incompatible data in self heal mode.

## 14.9.0

### Fixed

Rare exception during checking possibility of usage of optimized version of prefix based remove when so far unseen objects was used as key in IDictionary

## 14.8.0

### Changed

Dumping JsonLike output from TypeDescriptor is now more JSON compliant.

## 14.7.0

### Added

Delegate constrains are now supported in C# 7.3, so it now makes compile time errors instead of runtime where possible.

### Fixed

Rare failure in IOC when running in parallel.
Better exception message when types of fields in Deserialization are different.

## 14.6.0

### Added

Relations could be now iterated.

## 14.5.2

### Fixed

Event Deserialization does not eagerly require Types exists in List and Dictionary.
Iterator fix for back reference in inlined lists and dictionaries. Now really works ;-)

## 14.5.1

### Fixed

Shut up Coverity. DiskChunkCache findings are false positives.
Iterator fix for back reference in inlined lists and dictionaries.
EventStore2Layer Deserializer bug in specific case.

## 14.5.0

### Added

Allow to use DateTime.MinValue and DateTime.MaxValue in ordered context - they will be automatically converted to UTC.

## 14.4.0

### Fixed

ListBy... methods in relations now correct type of AdvancedEnumeratorParam<T>

## 14.3.0

### Fixed

Made order of properties in EventSerializers stable by sorting them by name.

## 14.2.1

### Fixed

EventLayer2 serialization of `Dictionary<int, Dictionary<int, ComplexObject>>` property.

## 14.2.0

### Changed

IOC RegisterInstance<T>(object value) now must be explicitly used so new RegisterInstance(object value) could be used.

## 14.1.0

### Added

IOC RegisterInstance(object value) overload.

## 14.0.0

### Added

RollbackAdvised property on KV and Object transactions interfaces to simplify notification of some infrastructure code to rollback transaction instead of committing it.

Relations support inheriting of methods from other interfaces.

### Breaking change

IOC RegisterInstance<T>(T value) now allows also value types as T, and value is not registered as value.GetType() but as typeof(T), which is same behavior as AutoFac.

## 13.1.0

### Added

Possibility to limit number of removed items at once in prefix based remove in relations.

## 13.0.0

### Added

Ported to .NetCore 2.0.

Simplified Nuget. Now it embed pdb and with SourceLink. GitHub releases contains zipped BTDB sources.

New releaser project to automate releasing.

## 12.7.0.0

### Fixed

Compactor Inducing Latency for writting transaction is now capped.

## 12.6.1.0

### Fixed

Fixed Nullable support in EventStore2Layer.

## 12.6.0.0

### Fixed

Uninterupted influx of data after db opening can prevent compactor from running.

## 12.5.1.0

### Fixed

IPAddress can now serialize and deserialize null value.

## 12.5.0.0

### Added

- Synchronization lock in EventLayer2 Deserialization to be on safe side.

## 12.4.0.0

### Added

- PersistedNameAttribute is supported on Apart Fields in relation interfaces

## 12.3.0.0

### Added

- IOC now resolves optional parameters that are not registered with its provided value

### Fixed

- Fixed problem with calculating index from older version value in specific case

## 12.2.0.0

### Added

- new method DeleteAllData() on ObjectDBTransaction
- PersistedNameAttribute is additionally allowed on interfaces - useful for Relations

## 12.1.0.0

### Added

- Event deserialization now automatically converts Enums to integer types.

## 12.0.0.0

### Added

- Changelog
- Nullable support in both ODb and EventStore
