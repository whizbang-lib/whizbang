# .NET Attributes Reference Guide

A comprehensive guide to all 496 unique C# attributes found in the .NET runtime source code.

## Overview

This reference catalog documents every attribute class defined in the dotnet/runtime repository, organized by namespace with practical guidance on when and how to use each attribute.

**Statistics:**
- **Total Attributes:** 496
- **Total Namespaces:** 71
- **Source:** dotnet/runtime repository

## Quick Navigation

Jump to a namespace:

- [System](#system) (11 attributes)
- [System.CodeDom.Compiler](#systemcodedomcompiler) (1 attribute)
- [System.ComponentModel](#systemcomponentmodel) (46 attributes)
- [System.ComponentModel.Composition](#systemcomponentmodelcomposition) (12 attributes)
- [System.ComponentModel.DataAnnotations](#systemcomponentmodeldataannotations) (32 attributes)
- [System.ComponentModel.DataAnnotations.Schema](#systemcomponentmodeldataannotationsschema) (7 attributes)
- [System.ComponentModel.Design](#systemcomponentmodeldesign) (1 attribute)
- [System.ComponentModel.Design.Serialization](#systemcomponentmodeldesignserialization) (3 attributes)
- [System.Composition](#systemcomposition) (3 attributes)
- [System.Configuration](#systemconfiguration) (25 attributes)
- [System.Data](#systemdata) (1 attribute)
- [System.Data.Common](#systemdatacommon) (2 attributes)
- [System.Data.Odbc](#systemdataodbc) (1 attribute)
- [System.Data.OleDb](#systemdataoledb) (1 attribute)
- [System.Data.OracleClient](#systemdataoracleclient) (1 attribute)
- [System.Data.SqlClient](#systemdatasqlclient) (1 attribute)
- [System.Diagnostics](#systemdiagnostics) (11 attributes)
- [System.Diagnostics.CodeAnalysis](#systemdiagnosticscodeanalysis) (17 attributes)
- [System.Diagnostics.Contracts](#systemdiagnosticscontracts) (9 attributes)
- [System.Diagnostics.Tracing](#systemdiagnosticstracing) (13 attributes)
- [System.DirectoryServices](#systemdirectoryservices) (1 attribute)
- [System.DirectoryServices.Protocols](#systemdirectoryservicesprotocols) (1 attribute)
- [System.Drawing.Printing](#systemdrawingprinting) (1 attribute)
- [System.Net](#systemnet) (2 attributes)
- [System.Net.Mail](#systemnetmail) (1 attribute)
- [System.Net.NetworkInformation](#systemnetnetworkinformation) (1 attribute)
- [System.Net.PeerToPeer](#systemnetpeertopeer) (1 attribute)
- [System.Net.PeerToPeer.Collaboration](#systemnetpeertopeer) (1 attribute)
- [System.Reflection](#systemreflection) (11 attributes)
- [System.Reflection.Metadata](#systemreflectionmetadata) (1 attribute)
- [System.Resources](#systemresources) (2 attributes)
- [System.Runtime](#systemruntime) (3 attributes)
- [System.Runtime.CompilerServices](#systemruntimecompilerservices) (73 attributes)
- [System.Runtime.ConstrainedExecution](#systemruntimeconstrainedexecution) (2 attributes)
- [System.Runtime.ExceptionServices](#systemruntimeexceptionservices) (1 attribute)
- [System.Runtime.InteropServices](#systemruntimeinteropservices) (50 attributes)
- [System.Runtime.InteropServices.JavaScript](#systemruntimeinteropservicesjavascript) (3 attributes)
- [System.Runtime.InteropServices.Marshalling](#systemruntimeinteropservicesmarshalling) (10 attributes)
- [System.Runtime.InteropServices.ObjectiveC](#systemruntimeinteropservicesobjectivec) (1 attribute)
- [System.Runtime.Serialization](#systemruntimeserialization) (12 attributes)
- [System.Runtime.Versioning](#systemruntimeversioning) (13 attributes)
- [System.Security](#systemsecurity) (7 attributes)
- [System.Security.Cryptography.X509Certificates](#systemsecuritycryptographyx509certificates) (1 attribute)
- [System.Security.Cryptography.Xml](#systemsecuritycryptographyxml) (1 attribute)
- [System.Security.Permissions](#systemsecuritypermissions) (5 attributes)
- [System.ServiceProcess](#systemserviceprocess) (2 attributes)
- [System.Text.Json.Serialization](#systemtextjsonserialization) (17 attributes)
- [System.Text.RegularExpressions](#systemtextregularexpressions) (1 attribute)
- [System.Timers](#systemtimers) (1 attribute)
- [System.Transactions](#systemtransactions) (1 attribute)
- [System.Web](#systemweb) (1 attribute)
- [System.Windows.Markup](#systemwindowsmarkup) (1 attribute)
- [System.Xml](#systemxml) (2 attributes)
- [System.Xml.Schema](#systemxmlschema) (2 attributes)
- [System.Xml.Serialization](#systemxmlserialization) (23 attributes)
- [Microsoft.Extensions.Configuration](#microsoftextensionsconfiguration) (1 attribute)
- [Microsoft.Extensions.Configuration.UserSecrets](#microsoftextensionsconfigurationusersecrets) (1 attribute)
- [Microsoft.Extensions.DependencyInjection](#microsoftextensionsdependencyinjection) (3 attributes)
- [Microsoft.Extensions.Logging](#microsoftextensionslogging) (2 attributes)
- [Microsoft.Extensions.Options](#microsoftextensionsoptions) (3 attributes)

---

## System

### AttributeUsage
**When to use:** Define how your custom attribute can be applied (to classes, methods, properties, etc.) and whether it can be used multiple times or inherited.

**Example use case:** Creating a custom validation attribute and specifying it can only be applied to properties.

### CLSCompliant
**When to use:** Mark assemblies, types, or members as compliant with the Common Language Specification, ensuring they can be used from any .NET language.

**Example use case:** Building a library that will be consumed by F# or VB.NET developers.

### Flags
**When to use:** Indicate that an enum should be treated as a bit field (flags), allowing bitwise combinations of its values.

**Example use case:** Creating an enum for file permissions where multiple permissions can be combined (Read | Write | Execute).

### LoaderOptimization
**When to use:** Specify how the runtime should optimize assembly loading for multi-domain scenarios.

**Example use case:** Optimizing assembly sharing across application domains (rare in modern .NET).

### MTAThread
**When to use:** Indicate that the main thread should use the Multi-Threaded Apartment COM model.

**Example use case:** COM interop scenarios requiring MTA threading (alternative to STAThread).

### NonSerialized
**When to use:** Exclude a field from binary serialization when using BinaryFormatter or similar serializers.

**Example use case:** Marking transient fields like caches or derived values that shouldn't be serialized.

### Obsolete
**When to use:** Mark APIs as deprecated, optionally specifying a message and whether usage should cause a compiler error.

**Example use case:** Deprecating an old method while guiding developers to use a newer alternative.

### ParamArray
**When to use:** Allow a method to accept a variable number of arguments (in C#, this is the `params` keyword).

**Example use case:** Creating utility methods like `string.Format()` that accept variable arguments.

### Serializable
**When to use:** Mark a type as eligible for binary serialization (legacy, avoid in modern code).

**Example use case:** Enabling BinaryFormatter serialization (deprecated in .NET 5+).

### STAThread
**When to use:** Indicate that the main thread should use the Single-Threaded Apartment COM model.

**Example use case:** Windows Forms or WPF applications that require STA threading for UI components.

### ThreadStatic
**When to use:** Make a static field unique per thread, giving each thread its own copy of the field.

**Example use case:** Thread-local storage for context information without using ThreadLocal<T>.

---

## System.CodeDom.Compiler

### GeneratedCode
**When to use:** Mark code as generated by a tool, allowing analysis tools and code coverage to exclude it.

**Example use case:** Marking source-generated code to exclude it from code review or coverage metrics.

---

## System.ComponentModel

### AmbientValue
**When to use:** Specify an ambient value for a property in design-time scenarios, indicating a value from the surrounding context.

**Example use case:** Defining default values for UI controls that inherit from parent containers.

### Bindable
**When to use:** Indicate whether a property should be available for data binding in design-time tools.

**Example use case:** Marking properties in custom controls that should appear in Visual Studio's property binding dialogs.

### Browsable
**When to use:** Control whether a property appears in the Properties window in design-time tools.

**Example use case:** Hiding internal properties from the Visual Studio Properties panel.

### Category
**When to use:** Specify the category name for grouping a property in the Properties window.

**Example use case:** Grouping related properties under "Appearance" or "Behavior" categories in the designer.

### ComplexBindingProperties
**When to use:** Specify which properties of a control are used for complex data binding (like DataSource and DataMember).

**Example use case:** Defining data source properties for custom grid or list controls.

### DataObject
**When to use:** Mark a class as suitable for use as a data source in design-time data binding scenarios.

**Example use case:** Creating business object classes that can be used with ObjectDataSource controls.

### DataObjectField
**When to use:** Provide metadata about fields in a data object, such as whether they are primary keys or identity fields.

**Example use case:** Marking ID properties in data transfer objects for ORM mapping.

### DataObjectMethod
**When to use:** Mark methods as data operations (Select, Insert, Update, Delete) for design-time data source wizards.

**Example use case:** Creating repository methods that can be discovered by Visual Studio's data source configuration.

### DefaultBindingProperty
**When to use:** Specify the default property for data binding when a control is dropped on a designer.

**Example use case:** Defining that a custom TextBox control binds to the Text property by default.

### DefaultEvent
**When to use:** Specify the default event for a component or control in design-time tools.

**Example use case:** Making double-clicking a button in the designer generate a Click event handler.

### DefaultProperty
**When to use:** Specify the default property for a component or control in design-time tools.

**Example use case:** Making the Text property the default property for a custom control.

### DefaultValue
**When to use:** Specify the default value for a property, used by designers and serializers to determine whether to serialize the value.

**Example use case:** Setting default values for control properties to avoid serializing unchanged values.

### Description
**When to use:** Provide a text description of a property, event, or method that appears in design-time tools.

**Example use case:** Adding helpful tooltips to properties in the Visual Studio Properties window.

### Designer
**When to use:** Specify a custom designer class for a component or control.

**Example use case:** Creating a custom visual designer for a complex control in Visual Studio.

### DesignerCategory
**When to use:** Specify the category of designer for a component (Component, Form, Designer, etc.).

**Example use case:** Indicating that a class should use the Windows Forms designer.

### DesignerSerializationVisibility
**When to use:** Control how a property is serialized by the designer (Visible, Hidden, Content).

**Example use case:** Preventing designer from serializing collection contents or hiding read-only properties.

### DesignOnly
**When to use:** Indicate that a property is only used at design time and should not be set at runtime.

**Example use case:** Marking properties that configure design-time behavior but have no runtime effect.

### DesignTimeVisible
**When to use:** Control whether a component appears in the component tray of the designer.

**Example use case:** Hiding non-visual components from the designer toolbox.

### DisplayName
**When to use:** Specify a custom display name for a property, event, or method in design-time tools.

**Example use case:** Showing "Background Color" instead of "BackgroundColor" in the Properties window.

### EditorBrowsable
**When to use:** Control whether a property, method, or class appears in IntelliSense in editors.

**Example use case:** Hiding advanced or legacy APIs from IntelliSense while keeping them accessible.

### Editor
**When to use:** Specify a custom UI editor for a property in design-time tools.

**Example use case:** Providing a color picker or file dialog for property editing in Visual Studio.

### ExtenderProvidedProperty
**When to use:** Mark a property as provided by an extender provider (rare, auto-applied).

**Example use case:** Properties added by ToolTip or ErrorProvider components.

### ImmutableObject
**When to use:** Indicate that an object's properties cannot be changed once set.

**Example use case:** Marking value types or immutable reference types to optimize designer behavior.

### InitializationEvent
**When to use:** Specify an event that signals when a component has completed initialization.

**Example use case:** Defining initialization events for custom components.

### InstallerType
**When to use:** Specify a custom installer class for a component.

**Example use case:** Providing custom installation logic for Windows Services or components.

### LicenseProvider
**When to use:** Specify a license provider for a licensed component.

**Example use case:** Implementing software licensing for commercial controls.

### ListBindable
**When to use:** Indicate whether a type can be used as a data source for list binding.

**Example use case:** Marking business object collections as bindable to list controls.

### Localizable
**When to use:** Indicate that a property's value should be localized in multi-language applications.

**Example use case:** Marking Text properties of controls for localization in internationalized apps.

### LookupBindingProperties
**When to use:** Specify properties used for lookup binding (like ComboBox DataSource and ValueMember).

**Example use case:** Defining data binding properties for custom lookup controls.

### MergableProperty
**When to use:** Indicate whether a property can be merged in the Properties window when multiple objects are selected.

**Example use case:** Allowing font properties to be set for multiple controls at once.

### NotifyParentProperty
**When to use:** Indicate that changing a nested property should notify the parent property to refresh.

**Example use case:** Refreshing parent property display when a sub-property in a complex object changes.

### ParenthesizePropertyName
**When to use:** Indicate that a property name should be displayed with parentheses in design-time tools.

**Example use case:** Visual distinction for special properties like "(Name)" in the Properties window.

### PasswordPropertyText
**When to use:** Indicate that a property contains password text and should be displayed with masking characters.

**Example use case:** Masking password fields in property grids.

### PropertyTab
**When to use:** Specify a custom property tab for a component in design-time tools.

**Example use case:** Adding custom property pages to the Properties window.

### ProvideProperty
**When to use:** Mark a method as providing an extended property to other components.

**Example use case:** Creating extender providers like ToolTip or ErrorProvider.

### Provider
**When to use:** Specify a type provider for a component.

**Example use case:** Custom type provider scenarios (rare).

### ReadOnly
**When to use:** Indicate that a property cannot be changed at design time or through property grids.

**Example use case:** Marking calculated or derived properties as read-only.

### RecommendedAsConfigurable
**When to use:** Indicate that a property should be bound to application settings (obsolete, use SettingsBindable).

**Example use case:** Legacy code marking properties for configuration binding.

### RefreshProperties
**When to use:** Specify when dependent properties should refresh in the Properties window after a value changes.

**Example use case:** Refreshing related properties when a key property changes (like changing units).

### RunInstaller
**When to use:** Indicate that a custom installer should be invoked when an assembly is installed.

**Example use case:** Marking installer classes for Windows Services or components.

### SettingsBindable
**When to use:** Indicate that a property supports binding to application settings.

**Example use case:** Enabling property binding to app.config or user settings.

### ToolboxItem
**When to use:** Control whether and how a component appears in the Visual Studio toolbox.

**Example use case:** Hiding or customizing the appearance of controls in the toolbox.

### ToolboxItemFilter
**When to use:** Filter which designer hosts can use a component from the toolbox.

**Example use case:** Restricting a control to only work with Windows Forms designers.

### TypeConverter
**When to use:** Specify a custom type converter for converting property values to/from strings.

**Example use case:** Converting complex types to/from strings for property grid editing.

### TypeDescriptionProvider
**When to use:** Specify a custom type description provider for advanced metadata scenarios.

**Example use case:** Providing custom metadata for dynamic types or proxy objects.

---

## System.ComponentModel.Composition

### Export
**When to use:** Mark a class, method, property, or field as exported for Managed Extensibility Framework (MEF) discovery.

**Example use case:** Exporting plugin implementations to be discovered by a host application.

### ExportMetadata
**When to use:** Attach metadata to an MEF export for filtering or querying during composition.

**Example use case:** Adding version or capability metadata to plugin exports.

### Import
**When to use:** Mark a property, field, or parameter for MEF dependency injection.

**Example use case:** Importing required services or plugins into a host application.

### ImportingConstructor
**When to use:** Mark a constructor to be used by MEF for dependency injection when creating instances.

**Example use case:** Injecting dependencies into a plugin via constructor parameters.

### ImportMany
**When to use:** Import all exports matching a contract, creating a collection of implementations.

**Example use case:** Importing all available plugins implementing a specific interface.

### InheritedExport
**When to use:** Mark a base class or interface so that all derived types are automatically exported.

**Example use case:** Auto-exporting all classes implementing a plugin interface.

### MetadataViewImplementation
**When to use:** Specify a concrete class to use for a metadata view interface in MEF.

**Example use case:** Providing custom metadata view implementations.

### PartCreationPolicy
**When to use:** Specify the lifetime policy for an MEF part (Shared, NonShared, Any).

**Example use case:** Creating singleton plugins (Shared) vs. instance-per-import (NonShared).

### PartMetadata
**When to use:** Attach metadata directly to an MEF part definition.

**Example use case:** Adding descriptive metadata to exported parts.

### PartNotDiscoverable
**When to use:** Prevent a part from being discovered automatically by MEF catalogs.

**Example use case:** Requiring explicit import rather than automatic discovery.

### CatalogReflectionContext
**When to use:** Specify a custom reflection context for MEF catalog part discovery.

**Example use case:** Customizing how MEF discovers and interprets parts.

---

## System.ComponentModel.DataAnnotations

### AllowedValues
**When to use:** Specify a whitelist of allowed values for a property (validation).

**Example use case:** Restricting a status field to specific valid values.

### Association
**When to use:** Define a relationship between entities in data models.

**Example use case:** Mapping foreign key relationships in LINQ to SQL or Entity Framework.

### Base64String
**When to use:** Validate that a string is valid Base64 encoding.

**Example use case:** Validating Base64-encoded file uploads or tokens.

### Compare
**When to use:** Validate that two properties have matching values.

**Example use case:** Password confirmation fields where Password and ConfirmPassword must match.

### ConcurrencyCheck
**When to use:** Mark a property for optimistic concurrency checking in Entity Framework.

**Example use case:** Preventing lost updates by checking timestamps or version numbers.

### CreditCard
**When to use:** Validate that a string matches credit card number format.

**Example use case:** Client-side validation of payment form inputs.

### CustomValidation
**When to use:** Specify a custom validation method for complex validation logic.

**Example use case:** Implementing business rule validation that spans multiple properties.

### DataType
**When to use:** Provide hints about the semantic type of data for rendering and validation.

**Example use case:** Marking properties as EmailAddress, PhoneNumber, or Currency for UI scaffolding.

### DeniedValues
**When to use:** Specify a blacklist of disallowed values for a property (validation).

**Example use case:** Preventing specific reserved words or forbidden values.

### Display
**When to use:** Specify display metadata (name, description, group, order) for UI scaffolding.

**Example use case:** Customizing how properties appear in auto-generated forms.

### DisplayColumn
**When to use:** Specify which column should represent an entity in dropdowns or references.

**Example use case:** Showing "CustomerName" instead of "CustomerId" in related entity dropdowns.

### DisplayFormat
**When to use:** Control how a property value is formatted for display and editing.

**Example use case:** Formatting dates, currencies, or numbers with specific patterns.

### Editable
**When to use:** Indicate whether a property can be edited in auto-generated forms.

**Example use case:** Making read-only properties like Created Date or ID non-editable.

### EmailAddress
**When to use:** Validate that a string is a valid email address format.

**Example use case:** Validating email input fields in registration forms.

### EnumDataType
**When to use:** Associate an enum type with a property for validation and UI scaffolding.

**Example use case:** Binding dropdown lists to enum values.

### FileExtensions
**When to use:** Validate that a file name has an allowed extension.

**Example use case:** Restricting file uploads to specific types (e.g., .jpg, .png, .pdf).

### FilterUIHint
**When to use:** Specify a template to use for rendering filter UI in dynamic data applications.

**Example use case:** Customizing filter controls in ASP.NET Dynamic Data grids.

### Key
**When to use:** Mark a property as the primary key for an entity in Entity Framework.

**Example use case:** Identifying the ID property in entity classes.

### Length
**When to use:** Validate the length of a string, array, or collection (minimum and maximum).

**Example use case:** Ensuring a username is between 3 and 20 characters.

### MaxLength
**When to use:** Validate that a string, array, or collection does not exceed a maximum length.

**Example use case:** Enforcing database column length constraints (e.g., VARCHAR(100)).

### MetadataType
**When to use:** Associate a metadata class with a partial class for adding validation attributes.

**Example use case:** Adding validation to auto-generated Entity Framework classes without modifying them.

### MinLength
**When to use:** Validate that a string, array, or collection meets a minimum length requirement.

**Example use case:** Requiring passwords to be at least 8 characters.

### Phone
**When to use:** Validate that a string is a valid phone number format.

**Example use case:** Validating phone number input fields.

### Range
**When to use:** Validate that a numeric value or date falls within a specified range.

**Example use case:** Ensuring age is between 18 and 120, or dates are within acceptable bounds.

### RegularExpression
**When to use:** Validate that a string matches a regular expression pattern.

**Example use case:** Validating ZIP codes, postal codes, or custom format requirements.

### Required
**When to use:** Validate that a property has a non-null, non-empty value.

**Example use case:** Marking mandatory fields in forms (Name, Email, etc.).

### ScaffoldColumn
**When to use:** Indicate whether a property should be included in auto-generated UI scaffolding.

**Example use case:** Hiding internal or system fields from generated forms.

### StringLength
**When to use:** Validate the length of a string (minimum and/or maximum).

**Example use case:** Enforcing length constraints on text fields.

### Timestamp
**When to use:** Mark a property as a row version for optimistic concurrency in Entity Framework.

**Example use case:** Using byte array timestamps for conflict detection.

### UIHint
**When to use:** Specify a template to use for rendering a property in dynamic UI scenarios.

**Example use case:** Customizing how a property is rendered in ASP.NET MVC or Dynamic Data.

### Url
**When to use:** Validate that a string is a valid URL format.

**Example use case:** Validating website or API endpoint input fields.

---

## System.ComponentModel.DataAnnotations.Schema

### Column
**When to use:** Specify database column details (name, order, type) for Entity Framework mapping.

**Example use case:** Mapping a property to a different column name in the database.

### ComplexType
**When to use:** Mark a class as a complex type (value object) in Entity Framework that doesn't have its own table.

**Example use case:** Embedded Address or Money value objects within an entity.

### DatabaseGenerated
**When to use:** Specify how a property value is generated by the database (Identity, Computed, None).

**Example use case:** Marking ID columns as auto-increment or timestamp columns as computed.

### ForeignKey
**When to use:** Specify the foreign key property for a navigation property in Entity Framework.

**Example use case:** Explicitly mapping OrderId to the Order navigation property.

### InverseProperty
**When to use:** Specify the inverse navigation property for bidirectional relationships in Entity Framework.

**Example use case:** Defining both sides of a parent-child relationship.

### NotMapped
**When to use:** Exclude a property from database mapping in Entity Framework.

**Example use case:** Calculated properties or transient fields that don't persist to the database.

### Table
**When to use:** Specify the database table name and schema for an entity in Entity Framework.

**Example use case:** Mapping an entity to a legacy table with a different name.

---

## System.ComponentModel.Design

### HelpKeyword
**When to use:** Specify a help keyword for context-sensitive help in design-time tools.

**Example use case:** Linking properties to help documentation in Visual Studio.

---

## System.ComponentModel.Design.Serialization

### DefaultSerializationProvider
**When to use:** Specify a default serialization provider for a type in design-time scenarios.

**Example use case:** Custom serialization for complex designer scenarios.

### DesignerSerializer
**When to use:** Specify a custom serializer for a type in design-time code generation.

**Example use case:** Customizing how controls are serialized in designer-generated code.

### RootDesignerSerializer
**When to use:** Specify a serializer for the root designer in design-time scenarios.

**Example use case:** Custom serialization for form or page designers.

---

## System.Composition

### ImportMetadataConstraint
**When to use:** Constrain MEF imports based on metadata values.

**Example use case:** Importing only plugins with specific version or capability metadata.

### OnImportsSatisfied
**When to use:** Mark a method to be called when all MEF imports are satisfied.

**Example use case:** Initialization logic that depends on all injected dependencies being available.

### Shared
**When to use:** Specify that an MEF export should have shared (singleton) lifetime.

**Example use case:** Creating singleton services in MEF composition.

### SharingBoundary
**When to use:** Define sharing boundaries for MEF parts in different scopes.

**Example use case:** Creating per-request or per-operation scopes in web applications.

---

## System.Configuration

### ApplicationScopedSetting
**When to use:** Mark a setting as application-scoped (read-only at runtime, shared by all users).

**Example use case:** Connection strings or application-wide configuration that doesn't change per user.

### CallbackValidator
**When to use:** Specify a callback method for custom validation logic in configuration.

**Example use case:** Complex validation rules for configuration values.

### ConfigurationCollection
**When to use:** Mark a property as representing a collection of configuration elements.

**Example use case:** Defining custom configuration sections with repeating elements.

### ConfigurationPermission
**When to use:** Specify code access security permissions for configuration access (obsolete in modern .NET).

**Example use case:** Legacy security scenarios (avoid in new code).

### ConfigurationProperty
**When to use:** Define a property in a custom configuration element or section.

**Example use case:** Creating strongly-typed configuration sections.

### ConfigurationValidator
**When to use:** Specify a validator for a configuration property.

**Example use case:** Ensuring configuration values meet specific criteria (range, regex, etc.).

### ConfigXml
**When to use:** Access raw XML content for a configuration element.

**Example use case:** Working with untyped or dynamic configuration XML.

### DefaultSettingValue
**When to use:** Specify the default value for an application setting.

**Example use case:** Providing fallback values for optional configuration settings.

### IntegerValidator
**When to use:** Validate that a configuration value is an integer within a specified range.

**Example use case:** Ensuring timeout or retry count settings are positive integers.

### LongValidator
**When to use:** Validate that a configuration value is a long integer within a specified range.

**Example use case:** Validating large numeric configuration values.

### NoSettingsVersionUpgrade
**When to use:** Prevent automatic upgrade of user settings during application updates.

**Example use case:** Maintaining stable settings across version updates.

### PositiveTimeSpanValidator
**When to use:** Validate that a configuration value is a positive TimeSpan.

**Example use case:** Ensuring timeout values are positive.

### RegexStringValidator
**When to use:** Validate that a configuration value matches a regular expression pattern.

**Example use case:** Validating format of configuration strings (emails, URLs, etc.).

### Setting
**When to use:** Define metadata for an application setting.

**Example use case:** Configuring application and user settings.

### SettingsDescription
**When to use:** Provide a description for a setting in application settings.

**Example use case:** Documenting the purpose of configuration values.

### SettingsGroupDescription
**When to use:** Provide a description for a settings group.

**Example use case:** Documenting groups of related settings.

### SettingsGroupName
**When to use:** Specify the group name for a setting.

**Example use case:** Organizing related settings into logical groups.

### SettingsManageability
**When to use:** Specify whether settings can be managed via Group Policy.

**Example use case:** Enterprise applications with centralized configuration management.

### SettingsProvider
**When to use:** Specify a custom provider for loading and saving settings.

**Example use case:** Storing settings in a database instead of XML files.

### SettingsSerializeAs
**When to use:** Specify how a setting value should be serialized (String, Xml, Binary).

**Example use case:** Controlling serialization format for complex setting values.

### SpecialSetting
**When to use:** Indicate the special type of a setting (ConnectionString, WebServiceUrl, etc.).

**Example use case:** Providing UI hints for specialized setting types.

### StringValidator
**When to use:** Validate that a configuration value is a string within length constraints.

**Example use case:** Enforcing minimum and maximum length for string settings.

### TimeSpanValidator
**When to use:** Validate that a configuration value is a TimeSpan within a specified range.

**Example use case:** Ensuring timeout settings are within acceptable bounds.

### TypeValidator
**When to use:** Validate that a configuration value can be converted to a specific type.

**Example use case:** Ensuring type safety for configuration values.

### UserScopedSetting
**When to use:** Mark a setting as user-scoped (writable at runtime, unique per user).

**Example use case:** User preferences like theme, window size, or personalization settings.

---

## System.Data

### DataSysDescription
**When to use:** Provide system descriptions for ADO.NET properties (internal).

**Example use case:** Internal framework usage for ADO.NET metadata.

---

## System.Data.Common

### DBDataPermission
**When to use:** Specify code access security permissions for database access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### DbProviderSpecificTypeProperty
**When to use:** Mark a property that returns a provider-specific type in ADO.NET.

**Example use case:** SqlDbType or OracleDbType properties in parameter classes.

---

## System.Data.Odbc

### OdbcPermission
**When to use:** Specify code access security permissions for ODBC access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Data.OleDb

### OleDbPermission
**When to use:** Specify code access security permissions for OLE DB access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Data.OracleClient

### OraclePermission
**When to use:** Specify code access security permissions for Oracle access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Data.SqlClient

### SqlClientPermission
**When to use:** Specify code access security permissions for SQL Server access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Diagnostics

### Conditional
**When to use:** Include method calls only when a specific compilation symbol is defined.

**Example use case:** Debug-only logging or diagnostics that are removed in Release builds.

### Debuggable
**When to use:** Specify debugging and JIT optimization modes for an assembly (compiler-applied).

**Example use case:** Compiler-generated attribute controlling debugging behavior.

### DebuggerBrowsable
**When to use:** Control how a member appears in debugger variable windows.

**Example use case:** Hiding internal or complex properties from debugger displays.

### DebuggerDisableUserUnhandledExceptions
**When to use:** Prevent the debugger from breaking on unhandled exceptions in specific code.

**Example use case:** Test frameworks handling expected exceptions.

### DebuggerDisplay
**When to use:** Customize how an object is displayed in debugger variable windows.

**Example use case:** Showing meaningful representations like "Person: John Doe (ID: 123)".

### DebuggerHidden
**When to use:** Hide methods from the debugger's call stack and prevent stepping into them.

**Example use case:** Internal plumbing code that developers shouldn't debug.

### DebuggerNonUserCode
**When to use:** Mark code as non-user code (generated or framework code) to skip during debugging.

**Example use case:** Source-generated code or framework internals.

### DebuggerStepperBoundary
**When to use:** Mark boundaries where the debugger should stop stepping through code.

**Example use case:** Boundaries between user code and framework code.

### DebuggerStepThrough
**When to use:** Indicate that the debugger should step over (not into) a method.

**Example use case:** Simple property wrappers or logging methods.

### DebuggerTypeProxy
**When to use:** Specify a custom type to display in place of this type in debugger windows.

**Example use case:** Simplifying display of complex collections or internal structures.

### DebuggerVisualizer
**When to use:** Specify a custom visualizer for a type in debugger windows.

**Example use case:** Custom visualizers for images, datasets, or domain objects.

### EventLogPermission
**When to use:** Specify code access security permissions for Event Log access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### MonitoringDescription
**When to use:** Provide descriptions for performance counter categories and counters.

**Example use case:** Documenting custom performance counters.

### PerformanceCounterPermission
**When to use:** Specify code access security permissions for performance counter access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### StackTraceHidden
**When to use:** Exclude methods from stack traces in exceptions.

**Example use case:** Helper methods or wrapper methods that clutter stack traces.

### Switch
**When to use:** Mark a field or property as a diagnostic switch for controlling tracing or logging.

**Example use case:** Runtime-configurable trace switches.

### SwitchLevel
**When to use:** Specify the default level for a diagnostic switch.

**Example use case:** Setting default trace levels.

---

## System.Diagnostics.CodeAnalysis

### ConstantExpected
**When to use:** Indicate that a parameter should be a compile-time constant value.

**Example use case:** API parameters that require constant values for performance or correctness.

### DynamicallyAccessedMembers
**When to use:** Indicate which members might be accessed via reflection, helping AOT and trimming tools.

**Example use case:** Marking types used with dependency injection or serialization.

### DynamicDependency
**When to use:** Declare a dependency on members that might be accessed via reflection.

**Example use case:** Preserving members for reflection that would otherwise be trimmed.

### ExcludeFromCodeCoverage
**When to use:** Exclude code from code coverage analysis.

**Example use case:** Generated code, trivial methods, or unreachable error paths.

### Experimental
**When to use:** Mark APIs as experimental, requiring opt-in via suppression to use.

**Example use case:** Preview features that may change or be removed in future versions.

### FeatureGuard
**When to use:** Indicate that a method guards access to a feature based on a condition.

**Example use case:** Runtime feature detection methods.

### FeatureSwitchDefinition
**When to use:** Define a feature switch for enabling/disabling functionality at compile or runtime.

**Example use case:** Feature flags for conditional compilation or trimming.

### RequiresAssemblyFiles
**When to use:** Indicate that code requires access to assembly files on disk (incompatible with single-file publish).

**Example use case:** Code that loads plugins or reads embedded resources from files.

### RequiresDynamicCode
**When to use:** Indicate that code requires dynamic code generation (incompatible with AOT).

**Example use case:** Reflection.Emit usage or runtime code generation.

### RequiresUnreferencedCode
**When to use:** Indicate that code uses reflection in ways that are incompatible with trimming.

**Example use case:** JSON serialization, dependency injection, or dynamic proxies.

### SetsRequiredMembers
**When to use:** Indicate that a constructor or method sets all required members of a type.

**Example use case:** Alternative constructors that initialize required properties.

### StringSyntax
**When to use:** Provide hints about string content format (Regex, Json, Uri, etc.) for tooling support.

**Example use case:** Enabling syntax highlighting and validation for regex patterns in IDE.

### SuppressMessage
**When to use:** Suppress specific code analysis warnings for targeted code.

**Example use case:** Suppressing false positives or intentional violations with justification.

### UnconditionalSuppressMessage
**When to use:** Unconditionally suppress analysis messages, even in unreachable code.

**Example use case:** Suppressing warnings in dynamically accessed code.

### UnscopedRef
**When to use:** Indicate that a ref parameter or return value can escape the current scope.

**Example use case:** Advanced ref scenarios with Span<T> or ref structs.

---

## System.Diagnostics.Contracts

### ContractArgumentValidator
**When to use:** Mark a method as a contract argument validator for parameter validation.

**Example use case:** Reusable argument validation methods.

### ContractClass
**When to use:** Specify the contract class for an interface in Code Contracts.

**Example use case:** Defining invariants and preconditions for interfaces.

### ContractClassFor
**When to use:** Mark a class as the contract definition for a specific type.

**Example use case:** Implementing Code Contracts for interfaces.

### ContractInvariantMethod
**When to use:** Mark a method containing object invariants that must always be true.

**Example use case:** Defining class invariants for validation.

### ContractPublicPropertyName
**When to use:** Specify the public property name for a field in contracts.

**Example use case:** Mapping backing fields to properties in contracts.

### ContractReferenceAssembly
**When to use:** Mark an assembly as a contract reference assembly.

**Example use case:** Distributing contract assemblies separately.

### ContractRuntimeIgnored
**When to use:** Indicate that contracts should be ignored at runtime.

**Example use case:** Contract verification only at compile time.

### ContractVerification
**When to use:** Control contract verification behavior for a type or method.

**Example use case:** Disabling verification for specific problematic code.

### Pure
**When to use:** Indicate that a method has no side effects and its return value depends only on inputs.

**Example use case:** Marking query methods or computation functions for contract verification.

---

## System.Diagnostics.Tracing

### Event
**When to use:** Mark a method as an ETW (Event Tracing for Windows) event in an EventSource.

**Example use case:** Defining structured logging events for performance analysis.

### EventData
**When to use:** Mark a struct or class as representing event payload data.

**Example use case:** Defining structured event payloads for ETW.

### EventField
**When to use:** Mark a field as part of an event payload with metadata.

**Example use case:** Adding custom formatting or tags to event fields.

### EventIgnore
**When to use:** Exclude a property or field from event payload serialization.

**Example use case:** Excluding transient or sensitive data from events.

### EventSource
**When to use:** Mark a class as an ETW event source for structured logging and diagnostics.

**Example use case:** Creating custom event sources for application telemetry.

### NonEvent
**When to use:** Mark a method in an EventSource class as not being an event.

**Example use case:** Helper methods in EventSource classes that shouldn't emit events.

---

## System.DirectoryServices

### DirectoryServicesPermission
**When to use:** Specify code access security permissions for directory services access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.DirectoryServices.Protocols

### Directory
**When to use:** Internal attribute for directory services protocols (internal use).

**Example use case:** Framework-internal usage.

---

## System.Drawing.Printing

### PrintingPermission
**When to use:** Specify code access security permissions for printing (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Net

### DnsPermission
**When to use:** Specify code access security permissions for DNS access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### SocketPermission
**When to use:** Specify code access security permissions for socket access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### WebPermission
**When to use:** Specify code access security permissions for web access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Net.Mail

### SmtpPermission
**When to use:** Specify code access security permissions for SMTP access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Net.NetworkInformation

### NetworkInformationPermission
**When to use:** Specify code access security permissions for network information access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Net.PeerToPeer

### PnrpPermission
**When to use:** Specify code access security permissions for PNRP (Peer Name Resolution Protocol) access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Net.PeerToPeer.Collaboration

### PeerCollaborationPermission
**When to use:** Specify code access security permissions for peer collaboration access (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Reflection

### AssemblyAlgorithmId
**When to use:** Specify the hash algorithm used to sign the assembly (compiler-applied).

**Example use case:** Strong-name signing configuration.

### AssemblyCompany
**When to use:** Specify the company name for the assembly.

**Example use case:** Setting company metadata for published libraries or applications.

### AssemblyConfiguration
**When to use:** Specify the build configuration (Debug, Release) for the assembly.

**Example use case:** Embedding build configuration in assembly metadata.

### AssemblyCopyright
**When to use:** Specify the copyright notice for the assembly.

**Example use case:** Adding copyright information to libraries or applications.

### AssemblyCulture
**When to use:** Specify the culture for a satellite assembly (compiler-applied for satellite assemblies).

**Example use case:** Localized resource assemblies.

### AssemblyDefaultAlias
**When to use:** Specify a default alias for the assembly for use in extern alias declarations.

**Example use case:** Avoiding naming conflicts when referencing multiple versions.

### AssemblyDelaySign
**When to use:** Specify whether the assembly uses delay signing (compiler-applied).

**Example use case:** Strong-name signing workflow for large organizations.

### AssemblyDescription
**When to use:** Specify a description of the assembly.

**Example use case:** Providing assembly metadata visible in file properties.

### AssemblyFileVersion
**When to use:** Specify the Win32 file version for the assembly.

**Example use case:** Setting file version for Windows Explorer display.

### AssemblyFlags
**When to use:** Specify assembly flags (compiler-applied).

**Example use case:** Internal compiler usage.

### AssemblyInformationalVersion
**When to use:** Specify the product version (informational version) for the assembly.

**Example use case:** Embedding semantic version or Git commit info.

### AssemblyKeyFile
**When to use:** Specify the key file for strong-name signing (compiler-applied).

**Example use case:** Strong-name signing configuration.

### AssemblyKeyName
**When to use:** Specify the key container name for strong-name signing (compiler-applied).

**Example use case:** Strong-name signing using key containers.

### AssemblyMetadata
**When to use:** Add custom key-value metadata to an assembly.

**Example use case:** Embedding build timestamps, Git commit hashes, or custom metadata.

### AssemblyProduct
**When to use:** Specify the product name for the assembly.

**Example use case:** Setting product metadata visible in file properties.

### AssemblySignatureKey
**When to use:** Specify signature key information for enhanced strong naming (compiler-applied).

**Example use case:** Enhanced strong-name signing.

### AssemblyTargetedPatchBand
**When to use:** Specify the target patch band for the assembly (obsolete).

**Example use case:** Legacy versioning scenarios (avoid in new code).

### AssemblyTitle
**When to use:** Specify a friendly title for the assembly.

**Example use case:** Setting assembly title visible in file properties.

### AssemblyTrademark
**When to use:** Specify trademark information for the assembly.

**Example use case:** Adding trademark notices to commercial products.

### AssemblyVersion
**When to use:** Specify the assembly version (major.minor.build.revision).

**Example use case:** Versioning assemblies for compatibility and binding.

### DefaultMember
**When to use:** Specify the default member (indexer) for a type.

**Example use case:** Defining default indexed properties.

### Obfuscation
**When to use:** Provide hints to obfuscation tools about how to treat a type or member.

**Example use case:** Protecting intellectual property in commercial software.

### ObfuscateAssembly
**When to use:** Indicate whether an assembly should be obfuscated.

**Example use case:** Enabling obfuscation for entire assemblies.

---

## System.Reflection.Metadata

### MetadataUpdateHandler
**When to use:** Mark a type as a handler for hot reload metadata updates.

**Example use case:** Handling hot reload events in development scenarios.

---

## System.Resources

### NeutralResourcesLanguage
**When to use:** Specify the default culture for an assembly's resources.

**Example use case:** Enabling resource fallback to English when localized resources aren't available.

### SatelliteContractVersion
**When to use:** Specify the version of satellite assemblies to use.

**Example use case:** Decoupling satellite assembly versions from main assembly versions.

---

## System.Runtime

### AssemblyTargetedPatchBand
**When to use:** Specify the target patch band for the assembly (obsolete).

**Example use case:** Legacy versioning scenarios (avoid in new code).

### BypassReadyToRun
**When to use:** Disable ReadyToRun precompiled code for specific methods.

**Example use case:** Debugging or working around ReadyToRun issues.

### TargetedPatchingOptOut
**When to use:** Indicate that a method should not be inlined across assembly boundaries.

**Example use case:** Preserving method boundaries for servicing and patching.

---

## System.Runtime.CompilerServices

### AccessedThroughProperty
**When to use:** Mark a backing field as accessed through a specific property (compiler-applied).

**Example use case:** Compiler-generated metadata for property-backed fields.

### AsyncIteratorStateMachine
**When to use:** Mark async iterator state machine types (compiler-applied).

**Example use case:** Compiler-generated attribute for async iterators.

### AsyncMethodBuilder
**When to use:** Specify a custom async method builder for async methods or task-like types.

**Example use case:** Custom async types like ValueTask or custom task types.

### AsyncStateMachine
**When to use:** Mark async state machine types (compiler-applied).

**Example use case:** Compiler-generated attribute for async/await methods.

### CallConvCdecl
**When to use:** Specify cdecl calling convention for function pointers (compiler-applied).

**Example use case:** Unmanaged function pointer interop.

### CallConvFastcall
**When to use:** Specify fastcall calling convention for function pointers (compiler-applied).

**Example use case:** Unmanaged function pointer interop.

### CallConvMemberFunction
**When to use:** Specify member function calling convention (compiler-applied).

**Example use case:** C++ member function interop.

### CallConvStdcall
**When to use:** Specify stdcall calling convention for function pointers (compiler-applied).

**Example use case:** Windows API function pointer interop.

### CallConvSuppressGCTransition
**When to use:** Suppress GC transition for function pointers (compiler-applied).

**Example use case:** High-performance interop scenarios.

### CallConvThiscall
**When to use:** Specify thiscall calling convention for function pointers (compiler-applied).

**Example use case:** C++ instance method interop.

### CallerArgumentExpression
**When to use:** Capture the source code expression of an argument at the call site.

**Example use case:** Assertion and validation helpers that report argument expressions.

### CallerFilePath
**When to use:** Automatically capture the source file path of the caller.

**Example use case:** Logging and diagnostics showing where a method was called from.

### CallerLineNumber
**When to use:** Automatically capture the line number of the caller.

**Example use case:** Logging and diagnostics showing the exact line that called a method.

### CallerMemberName
**When to use:** Automatically capture the member name of the caller.

**Example use case:** INotifyPropertyChanged implementations that automatically get property names.

### CollectionBuilder
**When to use:** Specify a builder method for collection expression initialization.

**Example use case:** Custom collection types supporting collection expressions (C# 12+).

### CompExactlyDependsOn
**When to use:** Declare exact compilation dependencies (compiler-internal).

**Example use case:** Internal compiler usage.

### CompilationRelaxations
**When to use:** Specify compilation relaxation modes (compiler-applied).

**Example use case:** Compiler-generated attribute for optimization control.

### CompilerFeatureRequired
**When to use:** Indicate that code requires a specific compiler feature to consume.

**Example use case:** Preventing older compilers from consuming new language features incorrectly.

### CompilerGenerated
**When to use:** Mark types or members as generated by the compiler.

**Example use case:** Compiler-generated backing fields, closures, and state machines.

### CompilerGlobalScope
**When to use:** Mark types as belonging to the global scope (compiler-internal).

**Example use case:** Internal compiler usage.

### CompilerLoweringPreserve
**When to use:** Preserve symbols through compiler lowering transformations (compiler-internal).

**Example use case:** Internal compiler usage for debugging.

### CreateNewOnMetadataUpdate
**When to use:** Indicate that hot reload should create new instances instead of updating existing ones.

**Example use case:** Types that cannot be safely updated in place during hot reload.

### CustomConstant
**When to use:** Specify a custom constant value for a parameter (compiler-applied).

**Example use case:** Default parameter values with custom types.

### DateTimeConstant
**When to use:** Specify a DateTime constant value for a parameter (compiler-applied).

**Example use case:** DateTime default parameter values.

### DecimalConstant
**When to use:** Specify a decimal constant value for a parameter (compiler-applied).

**Example use case:** Decimal default parameter values.

### DefaultDependency
**When to use:** Specify default dependency loading behavior (obsolete).

**Example use case:** Legacy dependency loading scenarios.

### Dependency
**When to use:** Specify assembly dependencies for loading (obsolete).

**Example use case:** Legacy dependency loading scenarios.

### DisablePrivateReflection
**When to use:** Disable private reflection access to an assembly.

**Example use case:** Security-sensitive assemblies that should not expose internals via reflection.

### DisableRuntimeMarshalling
**When to use:** Disable automatic runtime marshalling for interop types.

**Example use case:** Manual marshalling scenarios for precise control over interop.

### Discardable
**When to use:** Mark types as discardable (internal usage).

**Example use case:** Internal compiler or runtime usage.

### Dynamic
**When to use:** Mark types or members that use the dynamic type (compiler-applied).

**Example use case:** Compiler-generated metadata for dynamic types.

### DynamicAttribute
**When to use:** Indicate dynamic type usage in signatures (compiler-applied).

**Example use case:** Compiler-generated metadata for dynamic types.

### EnumeratorCancellation
**When to use:** Automatically map a CancellationToken parameter to async enumerable cancellation.

**Example use case:** Async enumerable methods that support cancellation.

### Extension
**When to use:** Mark a method as an extension method (compiler-applied).

**Example use case:** Compiler-generated attribute for extension methods.

### ExtensionMarker
**When to use:** Mark types containing extension methods (compiler-internal).

**Example use case:** Internal compiler usage.

### FixedAddressValueType
**When to use:** Mark a value type as having a fixed address in memory (rare, unsafe scenarios).

**Example use case:** Advanced unsafe code scenarios.

### FixedBuffer
**When to use:** Mark fixed-size buffer fields in unsafe structs (compiler-applied).

**Example use case:** Compiler-generated attribute for fixed buffers.

### IndexerName
**When to use:** Specify a custom name for an indexer (compiler-applied).

**Example use case:** Overriding the default "Item" indexer name.

### InlineArray
**When to use:** Mark a struct as an inline array (C# 12+).

**Example use case:** Efficient fixed-size array allocations in structs.

### InternalsVisibleTo
**When to use:** Expose internal members to a specific assembly (friend assemblies).

**Example use case:** Allowing test assemblies to access internal APIs.

### InterpolatedStringHandler
**When to use:** Mark a type as a custom interpolated string handler.

**Example use case:** Custom string interpolation for logging or formatting.

### InterpolatedStringHandlerArgument
**When to use:** Specify which arguments from the containing method should be passed to an interpolated string handler.

**Example use case:** Context-aware string interpolation handlers.

### Intrinsic
**When to use:** Mark methods as compiler intrinsics that may be replaced with specialized IL (compiler-internal).

**Example use case:** Internal compiler optimizations.

### IsConst
**When to use:** Mark fields as const (compiler-applied).

**Example use case:** Compiler-generated metadata for const fields.

### IsByRefLike
**When to use:** Mark a type as a ref struct (compiler-applied).

**Example use case:** Compiler-generated attribute for ref structs like Span<T>.

### IsReadOnly
**When to use:** Mark types or methods as readonly (compiler-applied).

**Example use case:** Compiler-generated attribute for readonly structs and ref readonly methods.

### IsUnmanaged
**When to use:** Indicate that a type constraint requires unmanaged types (compiler-applied).

**Example use case:** Generic constraints requiring unmanaged types.

### IteratorStateMachine
**When to use:** Mark iterator state machine types (compiler-applied).

**Example use case:** Compiler-generated attribute for iterator methods (yield).

### MethodImpl
**When to use:** Control method implementation characteristics (inlining, synchronization, optimization).

**Example use case:** Marking methods for aggressive inlining or disabling inlining.

### ModuleInitializer
**When to use:** Mark a method to run automatically when the module is initialized.

**Example use case:** Module-level initialization code (like static constructors but for modules).

### Nullable
**When to use:** Provide nullable reference type annotations (compiler-applied).

**Example use case:** Compiler-generated metadata for nullable reference types.

### NullableContext
**When to use:** Specify the nullable context for a scope (compiler-applied).

**Example use case:** Compiler-generated nullable context metadata.

### NullablePublicOnly
**When to use:** Indicate that nullable annotations apply only to public APIs (compiler-applied).

**Example use case:** Compiler-generated nullable metadata.

### OverloadResolutionPriority
**When to use:** Control the priority of overload resolution for ambiguous calls.

**Example use case:** Preferring specific overloads in generic APIs.

### ParamCollection
**When to use:** Mark a parameter as accepting any collection type (C# 13+).

**Example use case:** Params parameters accepting any collection, not just arrays.

### PreserveBaseOverrides
**When to use:** Preserve base method overrides during runtime code modification.

**Example use case:** Hot reload scenarios preserving virtual method chains.

### ReferenceAssembly
**When to use:** Mark an assembly as a reference assembly (metadata-only, no IL).

**Example use case:** Compiler-generated reference assemblies for faster builds.

### RefSafetyRules
**When to use:** Specify ref safety rules version (compiler-applied).

**Example use case:** Compiler-generated ref safety metadata.

### RequiredMember
**When to use:** Mark properties or fields as required (must be initialized).

**Example use case:** Required properties that must be set during object initialization.

### RequiresLocation
**When to use:** Indicate that a parameter must have a storage location (compiler-applied).

**Example use case:** Ref or out parameter requirements.

### RuntimeCompatibility
**When to use:** Specify runtime compatibility settings (compiler-applied).

**Example use case:** Compiler-generated runtime compatibility metadata.

### ScopedRef
**When to use:** Indicate scoped lifetime for ref parameters or locals (compiler-applied).

**Example use case:** Compiler-generated ref safety metadata.

### SkipLocalsInit
**When to use:** Skip zero-initialization of local variables for performance.

**Example use case:** High-performance code where locals are always assigned before use.

### SpecialName
**When to use:** Mark members with special names (properties, events, operators) (compiler-applied).

**Example use case:** Compiler-generated metadata for special members.

### StateMachine
**When to use:** Mark state machine types for async or iterators (compiler-applied).

**Example use case:** Base for AsyncStateMachine and IteratorStateMachine.

### StringFreezing
**When to use:** Control string freezing behavior (obsolete).

**Example use case:** Legacy string optimization scenarios.

### SuppressIldasm
**When to use:** Prevent disassembly of an assembly using ildasm.exe.

**Example use case:** Obfuscation and intellectual property protection.

### TupleElementNames
**When to use:** Specify names for tuple elements (compiler-applied).

**Example use case:** Compiler-generated metadata for named tuple elements.

### TypeForwardedFrom
**When to use:** Indicate that a type was moved from another assembly.

**Example use case:** Maintaining compatibility when refactoring code across assemblies.

### TypeForwardedTo
**When to use:** Forward a type to another assembly for backward compatibility.

**Example use case:** Moving types between assemblies without breaking existing code.

### UnsafeAccessor
**When to use:** Access private members without reflection (C# 12+).

**Example use case:** High-performance access to private APIs for testing or integration.

### UnsafeAccessorType
**When to use:** Specify the type for UnsafeAccessor attribute.

**Example use case:** Accessing nested private types.

### UnsafeValueType
**When to use:** Mark value types as unsafe (internal).

**Example use case:** Internal runtime usage.

---

## System.Runtime.ConstrainedExecution

### PrePrepareMethod
**When to use:** Indicate that a method should be prepared for execution in constrained execution regions (obsolete).

**Example use case:** Legacy reliability scenarios (avoid in new code).

### ReliabilityContract
**When to use:** Specify reliability guarantees for code in constrained execution regions (obsolete).

**Example use case:** Legacy reliability scenarios (avoid in new code).

---

## System.Runtime.ExceptionServices

### HandleProcessCorruptedStateExceptions
**When to use:** Allow catching corrupted state exceptions (access violations, stack overflows) (dangerous).

**Example use case:** Very rare scenarios where corrupted state exceptions must be caught (usually bad design).

---

## System.Runtime.InteropServices

### AllowReversePInvokeCalls
**When to use:** Allow unmanaged code to call back into managed code (reverse P/Invoke).

**Example use case:** Callback functions passed to native APIs.

### AutomationProxy
**When to use:** Use the automation proxy for COM automation (obsolete).

**Example use case:** Legacy COM interop scenarios.

### BestFitMapping
**When to use:** Control whether characters without exact Unicode-to-ANSI mappings use best-fit mapping in marshalling.

**Example use case:** String marshalling in P/Invoke with specific encoding requirements.

### ClassInterface
**When to use:** Control the type of COM interface generated for a class.

**Example use case:** Customizing COM visibility and interface generation.

### CoClass
**When to use:** Specify the coclass implementation for a COM interface.

**Example use case:** COM interop mapping interfaces to implementations.

### ComAliasName
**When to use:** Specify the COM alias for a type in type libraries.

**Example use case:** Custom COM type library generation.

### ComCompatibleVersion
**When to use:** Specify the COM-compatible version for type library generation.

**Example use case:** COM versioning scenarios.

### ComConversionLoss
**When to use:** Indicate that information is lost when converting to COM (compiler-applied).

**Example use case:** Compiler-generated COM interop warnings.

### ComDefaultInterface
**When to use:** Specify the default interface for a COM class.

**Example use case:** COM interop defining primary interfaces.

### ComEventInterface
**When to use:** Map a .NET event interface to a COM source interface.

**Example use case:** COM event handling and connection points.

### ComImport
**When to use:** Mark an interface or class as imported from COM.

**Example use case:** Defining COM interfaces for P/Invoke and COM interop.

### ComVisible
**When to use:** Control whether a type or member is visible to COM.

**Example use case:** Exposing .NET types to COM clients or hiding internals.

### DefaultCharSet
**When to use:** Specify the default character set for P/Invoke in an assembly (obsolete).

**Example use case:** Legacy P/Invoke character set configuration.

### DefaultDllImportSearchPaths
**When to use:** Specify default DLL search paths for P/Invoke in an assembly.

**Example use case:** Security hardening to prevent DLL hijacking.

### DefaultParameterValue
**When to use:** Specify default values for COM optional parameters.

**Example use case:** COM interop with optional parameters.

### DispId
**When to use:** Specify the COM dispatch ID (DISPID) for a member.

**Example use case:** COM automation and IDispatch implementations.

### DllImport
**When to use:** Declare a method implemented in an unmanaged DLL (P/Invoke).

**Example use case:** Calling Windows APIs or native libraries.

### DynamicInterfaceCastableImplementation
**When to use:** Mark an interface implemented via IDynamicInterfaceCastable.

**Example use case:** Dynamic COM interface casting.

### FieldOffset
**When to use:** Specify the byte offset of a field in an explicit layout struct.

**Example use case:** Overlapping fields in unions or binary serialization layouts.

### Guid
**When to use:** Specify the GUID for a COM interface, class, or type library.

**Example use case:** COM interop requiring specific GUIDs.

### ImportedFromTypeLib
**When to use:** Indicate that an assembly was imported from a COM type library.

**Example use case:** COM interop wrapper assemblies.

### In
**When to use:** Mark a parameter as input-only in COM interop or for ref readonly semantics.

**Example use case:** Read-only ref parameters in interop.

### InterfaceType
**When to use:** Specify the type of COM interface (IUnknown, IDispatch, Dual).

**Example use case:** COM interface design for automation or dual interfaces.

### LCIDConversion
**When to use:** Specify locale ID (LCID) conversion for a method in COM interop.

**Example use case:** COM automation methods with locale support.

### LibraryImport
**When to use:** Declare a source-generated P/Invoke method (.NET 7+).

**Example use case:** AOT-compatible P/Invoke with source generation.

### ManagedToNativeComInteropStub
**When to use:** Specify a custom stub for managed-to-native COM interop.

**Example use case:** Custom COM marshalling scenarios.

### MarshalAs
**When to use:** Control how parameters, return values, or fields are marshalled in interop.

**Example use case:** Marshalling strings, arrays, or structures with specific formats.

### Optional
**When to use:** Mark a parameter as optional in COM interop.

**Example use case:** COM methods with optional parameters.

### Out
**When to use:** Mark a parameter as output-only in COM interop.

**Example use case:** Methods that return values through out parameters.

### PreserveSig
**When to use:** Preserve the native signature of a method instead of converting HRESULTs to exceptions.

**Example use case:** COM methods that return HRESULTs that should be handled manually.

### PrimaryInteropAssembly
**When to use:** Mark an assembly as the Primary Interop Assembly (PIA) for a COM type library.

**Example use case:** Official COM interop assemblies for type libraries.

### ProgId
**When to use:** Specify the ProgID for a COM class.

**Example use case:** COM class registration and creation.

### StructLayout
**When to use:** Control the memory layout of a struct or class for interop.

**Example use case:** P/Invoke structures, COM interop, or binary serialization.

### SuppressGCTransition
**When to use:** Suppress GC transitions for very short P/Invoke calls (performance optimization).

**Example use case:** High-frequency, extremely fast native calls.

### TypeIdentifier
**When to use:** Mark types for COM type equivalence (no-PIA).

**Example use case:** Embedding COM interop types without PIAs.

### TypeLibFunc
**When to use:** Specify type library function flags for COM interop.

**Example use case:** COM type library generation.

### TypeLibImportClass
**When to use:** Specify the default implementation class for an imported COM interface.

**Example use case:** COM interop default interface implementations.

### TypeLibType
**When to use:** Specify type library type flags for COM interop.

**Example use case:** COM type library generation.

### TypeLibVar
**When to use:** Specify type library variable flags for COM interop.

**Example use case:** COM type library generation.

### TypeLibVersion
**When to use:** Specify the version of the COM type library.

**Example use case:** COM type library versioning.

### TypeMap
**When to use:** Map types for interop scenarios (internal).

**Example use case:** Internal interop usage.

### TypeMapAssemblyTarget
**When to use:** Specify assembly targets for type mapping (internal).

**Example use case:** Internal interop usage.

### TypeMapAssociation
**When to use:** Associate types for interop mapping (internal).

**Example use case:** Internal interop usage.

### UnmanagedCallConv
**When to use:** Specify calling conventions for unmanaged function pointers.

**Example use case:** Function pointer interop with specific calling conventions.

### UnmanagedCallersOnly
**When to use:** Mark a method as callable from unmanaged code only (C# 9+).

**Example use case:** Callbacks to unmanaged code with specific calling conventions.

### UnmanagedFunctionPointer
**When to use:** Specify calling convention and marshalling for delegate-to-function-pointer conversions.

**Example use case:** Delegates used as callbacks in P/Invoke.

### WasmImportLinkage
**When to use:** Specify import linkage for WebAssembly interop.

**Example use case:** WebAssembly JavaScript interop.

---

## System.Runtime.InteropServices.JavaScript

### JSExport
**When to use:** Mark a method as exported to JavaScript in WebAssembly.

**Example use case:** Exposing .NET methods to JavaScript in Blazor WebAssembly.

### JSImport
**When to use:** Import a JavaScript function to call from .NET in WebAssembly.

**Example use case:** Calling browser APIs from Blazor WebAssembly.

### JSMarshalAs
**When to use:** Specify custom marshalling for JavaScript interop types.

**Example use case:** Custom marshalling for complex JavaScript types.

---

## System.Runtime.InteropServices.Marshalling

### ComExposedClass
**When to use:** Mark a class as exposed to COM with source-generated marshalling.

**Example use case:** COM interop with source generation.

### ContiguousCollectionMarshaller
**When to use:** Mark a marshaller for contiguous collections.

**Example use case:** Custom collection marshalling.

### CustomMarshaller
**When to use:** Specify a custom marshaller for a type in interop.

**Example use case:** Custom marshalling logic for complex types.

### GeneratedComClass
**When to use:** Mark a COM class for source generation (compiler-applied).

**Example use case:** Source-generated COM class wrappers.

### GeneratedComInterface
**When to use:** Mark a COM interface for source generation (compiler-applied).

**Example use case:** Source-generated COM interface wrappers.

### IUnknownDerived
**When to use:** Mark an interface as derived from IUnknown for COM interop.

**Example use case:** COM interface hierarchy.

### MarshalUsing
**When to use:** Specify a custom marshaller to use for a parameter or return value.

**Example use case:** Custom marshalling for specific parameters.

### NativeMarshalling
**When to use:** Specify a native marshaller for a type.

**Example use case:** Custom native type representations.

### UnmanagedObjectUnwrapper
**When to use:** Specify an unwrapper for unmanaged objects.

**Example use case:** Custom COM object unwrapping.

### VirtualMethodIndex
**When to use:** Specify the virtual method table index for a COM method.

**Example use case:** Manual COM interface definitions.

---

## System.Runtime.InteropServices.ObjectiveC

### ObjectiveCTrackedType
**When to use:** Mark a type for Objective-C reference tracking.

**Example use case:** Objective-C interop on macOS/iOS.

---

## System.Runtime.Serialization

### CollectionDataContract
**When to use:** Customize how a collection is serialized with DataContractSerializer.

**Example use case:** Custom collection serialization for WCF or DataContract scenarios.

### DataContract
**When to use:** Mark a type as serializable with DataContractSerializer.

**Example use case:** WCF service contracts or XML/JSON serialization.

### DataMember
**When to use:** Mark a property or field for inclusion in DataContract serialization.

**Example use case:** Explicit control over serialization membership.

### EnumMember
**When to use:** Specify the serialized value for an enum member in DataContract serialization.

**Example use case:** Mapping enum values to different strings in serialization.

### IgnoreDataMember
**When to use:** Exclude a property or field from DataContract serialization.

**Example use case:** Transient or calculated properties that shouldn't be serialized.

### KnownType
**When to use:** Specify derived types for polymorphic DataContract serialization.

**Example use case:** Serializing polymorphic object graphs with DataContractSerializer.

### OnDeserialized
**When to use:** Mark a method to run after deserialization completes.

**Example use case:** Initialization or validation logic after deserialization.

### OnDeserializing
**When to use:** Mark a method to run before deserialization begins.

**Example use case:** Preparing state before deserialization.

### OnSerialized
**When to use:** Mark a method to run after serialization completes.

**Example use case:** Cleanup or logging after serialization.

### OnSerializing
**When to use:** Mark a method to run before serialization begins.

**Example use case:** Preparing transient state before serialization.

### OptionalField
**When to use:** Mark fields as optional for versioning in binary serialization.

**Example use case:** Backward compatibility when adding new fields.

### ContractNamespace
**When to use:** Specify the XML namespace for DataContract types.

**Example use case:** Custom XML namespaces for web services.

---

## System.Runtime.Versioning

### ComponentGuarantees
**When to use:** Document stability guarantees for a component (stable, side-by-side, etc.).

**Example use case:** Documenting API stability for library consumers.

### NonVersionable
**When to use:** Prevent the runtime from version-redirecting a type.

**Example use case:** Types that must have exact version matches.

### ObsoletedOSPlatform
**When to use:** Mark APIs as obsolete on specific OS platforms or versions.

**Example use case:** Deprecating Windows 7-specific APIs.

### RequiresPreviewFeatures
**When to use:** Mark APIs that require preview feature opt-in.

**Example use case:** Experimental APIs in preview releases.

### ResourceConsumption
**When to use:** Indicate that a method consumes resources (obsolete).

**Example use case:** Legacy resource consumption documentation.

### ResourceExposure
**When to use:** Indicate that a method exposes resources (obsolete).

**Example use case:** Legacy resource exposure documentation.

### SupportedOSPlatform
**When to use:** Indicate that an API is supported only on specific OS platforms.

**Example use case:** Windows-only or Linux-only APIs.

### SupportedOSPlatformGuard
**When to use:** Mark a method as a platform guard for conditional compilation.

**Example use case:** Platform detection methods for conditional API usage.

### TargetFramework
**When to use:** Specify the target framework for an assembly (compiler-applied).

**Example use case:** Compiler-generated target framework metadata.

### TargetPlatform
**When to use:** Specify the target platform for an assembly.

**Example use case:** Platform-specific application metadata.

### UnsupportedOSPlatform
**When to use:** Indicate that an API is not supported on specific OS platforms.

**Example use case:** Marking APIs unavailable on mobile platforms.

### UnsupportedOSPlatformGuard
**When to use:** Mark a method as a guard for unsupported platforms.

**Example use case:** Platform detection for avoiding unsupported APIs.

---

## System.Security

### AllowPartiallyTrustedCallers
**When to use:** Allow partially trusted code to call into a strong-named assembly (obsolete).

**Example use case:** Legacy Code Access Security scenarios (avoid in new code).

### DynamicSecurityMethod
**When to use:** Mark methods as dynamic security methods (internal).

**Example use case:** Internal security infrastructure.

### SecurityCritical
**When to use:** Mark code as security-critical that can perform privileged operations (obsolete).

**Example use case:** Legacy Code Access Security scenarios (avoid in new code).

### SecurityRules
**When to use:** Specify the security rules version for an assembly (obsolete).

**Example use case:** Legacy Code Access Security scenarios (avoid in new code).

### SecuritySafeCritical
**When to use:** Mark security-critical code as safe for transparent code to call (obsolete).

**Example use case:** Legacy Code Access Security scenarios (avoid in new code).

### SecurityTransparent
**When to use:** Mark code as security-transparent that cannot perform privileged operations (obsolete).

**Example use case:** Legacy Code Access Security scenarios (avoid in new code).

### SecurityTreatAsSafe
**When to use:** Mark code as safe for security purposes (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### SuppressUnmanagedCodeSecurity
**When to use:** Skip security checks when calling unmanaged code (dangerous, use with extreme caution).

**Example use case:** High-performance interop where security checks are prohibitively expensive (rare).

### UnverifiableCode
**When to use:** Indicate that an assembly contains unverifiable code (obsolete).

**Example use case:** Legacy security scenarios with unsafe code.

---

## System.Security.Cryptography.X509Certificates

### X501
**When to use:** Internal attribute for X.501 distinguished names (internal usage).

**Example use case:** Internal X.509 certificate handling.

---

## System.Security.Cryptography.Xml

### CanonicalXml
**When to use:** Internal attribute for XML canonicalization (internal usage).

**Example use case:** XML signature canonicalization.

---

## System.Security.Permissions

### FileIOPermission
**When to use:** Specify code access security permissions for file I/O (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### IsolatedStorageFilePermission
**When to use:** Specify code access security permissions for isolated storage files (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### IsolatedStoragePermission
**When to use:** Specify code access security permissions for isolated storage (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### KeyContainerPermission
**When to use:** Specify code access security permissions for key containers (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### StorePermission
**When to use:** Specify code access security permissions for X.509 certificate stores (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### TypeDescriptorPermission
**When to use:** Specify code access security permissions for type descriptors (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.ServiceProcess

### ServiceControllerPermission
**When to use:** Specify code access security permissions for service controllers (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

### ServiceProcessDescription
**When to use:** Provide descriptions for service process properties in designers.

**Example use case:** Design-time metadata for Windows Service projects.

---

## System.Text.Json.Serialization

### Json
**When to use:** Base attribute for JSON serialization attributes (abstract).

**Example use case:** Base class for JSON attributes.

### JsonConstructor
**When to use:** Specify which constructor to use for JSON deserialization.

**Example use case:** Types with multiple constructors needing explicit deserialization constructor.

### JsonConverter
**When to use:** Specify a custom converter for JSON serialization/deserialization.

**Example use case:** Custom serialization logic for complex types.

### JsonDerivedType
**When to use:** Register derived types for polymorphic JSON serialization.

**Example use case:** Polymorphic object graphs with System.Text.Json.

### JsonExtensionData
**When to use:** Capture unrecognized JSON properties in a dictionary.

**Example use case:** Flexible JSON deserialization with extra properties.

### JsonIgnore
**When to use:** Exclude properties from JSON serialization.

**Example use case:** Transient or sensitive properties.

### JsonInclude
**When to use:** Include non-public properties in JSON serialization.

**Example use case:** Serializing private or internal properties.

### JsonNumberHandling
**When to use:** Control how numbers are read/written in JSON.

**Example use case:** Allowing numbers as strings or strict number handling.

### JsonObjectCreationHandling
**When to use:** Control how objects are created during deserialization (populate vs. replace).

**Example use case:** Populating existing collections instead of replacing them.

### JsonPolymorphic
**When to use:** Enable polymorphic serialization for a type hierarchy.

**Example use case:** Base classes with multiple derived types in JSON.

### JsonPropertyName
**When to use:** Specify the JSON property name for a property (different from C# name).

**Example use case:** Mapping C# properties to different JSON key names.

### JsonPropertyOrder
**When to use:** Control the order of properties in serialized JSON.

**Example use case:** Ensuring specific property ordering in JSON output.

### JsonRequired
**When to use:** Mark a property as required in JSON deserialization.

**Example use case:** Enforcing required properties in JSON schemas.

### JsonSerializable
**When to use:** Register a type for source-generated JSON serialization.

**Example use case:** AOT-compatible JSON serialization with source generation.

### JsonSourceGenerationOptions
**When to use:** Configure source generation options for JSON serialization.

**Example use case:** Customizing generated JSON serialization code.

### JsonStringEnumMemberName
**When to use:** Specify custom string values for enum members in JSON.

**Example use case:** Mapping enums to specific JSON string values.

### JsonUnmappedMemberHandling
**When to use:** Control how unmapped JSON properties are handled (ignore vs. disallow).

**Example use case:** Strict JSON deserialization rejecting unknown properties.

---

## System.Text.RegularExpressions

### GeneratedRegex
**When to use:** Generate optimized regex code at compile time (C# 11+).

**Example use case:** AOT-compatible, high-performance regex with source generation.

---

## System.Timers

### TimersDescription
**When to use:** Provide descriptions for timer properties in designers.

**Example use case:** Design-time metadata for Timer components.

---

## System.Transactions

### DistributedTransactionPermission
**When to use:** Specify code access security permissions for distributed transactions (obsolete).

**Example use case:** Legacy security scenarios (avoid in new code).

---

## System.Web

### AspNetHostingPermission
**When to use:** Specify code access security permissions for ASP.NET hosting (obsolete).

**Example use case:** Legacy ASP.NET security scenarios (avoid in new code).

---

## System.Windows.Markup

### ValueSerializer
**When to use:** Specify a custom value serializer for XAML serialization.

**Example use case:** Custom XAML serialization for WPF types.

---

## System.Xml

### Xml
**When to use:** Base attribute for XML attributes (abstract).

**Example use case:** Base class for XML serialization attributes.

### XmlUnspecified
**When to use:** Internal attribute for unspecified XML values (internal usage).

**Example use case:** Internal XML serialization.

---

## System.Xml.Schema

### XmlSchema
**When to use:** Internal attribute for XML schema (internal usage).

**Example use case:** Internal XML schema handling.

### XmlSchemaAny
**When to use:** Internal attribute for XML schema any elements (internal usage).

**Example use case:** Internal XML schema handling.

---

## System.Xml.Serialization

### Soap
**When to use:** Base attribute for SOAP serialization attributes (abstract).

**Example use case:** Base class for SOAP serialization.

### SoapElement
**When to use:** Specify XML element details for SOAP serialization.

**Example use case:** Custom SOAP XML element names.

### SoapEnum
**When to use:** Specify the SOAP representation of an enum value.

**Example use case:** Custom SOAP enum serialization.

### SoapIgnore
**When to use:** Exclude a member from SOAP serialization.

**Example use case:** Transient properties in SOAP services.

### SoapInclude
**When to use:** Include derived types in SOAP serialization.

**Example use case:** Polymorphic SOAP serialization.

### SoapType
**When to use:** Specify SOAP type details for a class.

**Example use case:** Custom SOAP type names and namespaces.

### XmlAny
**When to use:** Base attribute for XML any attributes (abstract).

**Example use case:** Base class for XML any elements/attributes.

### XmlAnyElement
**When to use:** Map a member to any XML element for flexible deserialization.

**Example use case:** Capturing unknown XML elements.

### XmlArray
**When to use:** Specify that a member should be serialized as an XML array.

**Example use case:** Wrapping collections in array elements.

### XmlArrayItem
**When to use:** Specify the element name for array items in XML serialization.

**Example use case:** Custom names for collection items.

### XmlChoiceIdentifier
**When to use:** Specify a field that identifies the type of a choice element.

**Example use case:** XML choice patterns with type discriminators.

### XmlElement
**When to use:** Specify XML element details for a property or field.

**Example use case:** Custom element names in XML serialization.

### XmlEnum
**When to use:** Specify the XML representation of an enum value.

**Example use case:** Custom XML enum values.

### XmlIgnore
**When to use:** Exclude a member from XML serialization.

**Example use case:** Transient properties.

### XmlInclude
**When to use:** Include derived types in XML serialization.

**Example use case:** Polymorphic XML serialization.

### XmlNamespaceDeclarations
**When to use:** Capture or specify XML namespace declarations.

**Example use case:** Custom XML namespace handling.

### XmlRoot
**When to use:** Specify the root element details for XML serialization.

**Example use case:** Custom root element names and namespaces.

### XmlSchemaProvider
**When to use:** Specify a method that provides XML schema for a type.

**Example use case:** Custom schema generation for complex types.

### XmlSerializerAssembly
**When to use:** Specify a pre-generated XML serializer assembly.

**Example use case:** Performance optimization with pre-generated serializers.

### XmlSerializerVersion
**When to use:** Specify versioning for XML serializers (obsolete).

**Example use case:** Legacy XML serializer versioning.

### XmlText
**When to use:** Map a member to XML text content of an element.

**Example use case:** Capturing element text content.

### XmlType
**When to use:** Specify XML type details for a class or struct.

**Example use case:** Custom XML type names and namespaces.

---

## Microsoft.Extensions.Configuration

### ConfigurationKeyName
**When to use:** Specify a custom configuration key name for a property.

**Example use case:** Mapping properties to different configuration keys.

---

## Microsoft.Extensions.Configuration.UserSecrets

### UserSecretsId
**When to use:** Specify the user secrets ID for an assembly.

**Example use case:** Storing development secrets outside of source control.

---

## Microsoft.Extensions.DependencyInjection

### ActivatorUtilitiesConstructor
**When to use:** Specify which constructor ActivatorUtilities should use for dependency injection.

**Example use case:** Types with multiple constructors needing explicit DI constructor.

### FromKeyedServices
**When to use:** Resolve services by key from keyed service collections (.NET 8+).

**Example use case:** Keyed dependency injection scenarios.

### ServiceKey
**When to use:** Specify a service key for keyed service registration (.NET 8+).

**Example use case:** Multiple implementations of the same service.

---

## Microsoft.Extensions.Logging

### LoggerMessage
**When to use:** Generate high-performance logging methods with source generation.

**Example use case:** Structured logging with compile-time code generation.

### ProviderAlias
**When to use:** Specify an alias for a logging provider for configuration.

**Example use case:** Custom logging provider configuration.

---

## Microsoft.Extensions.Options

### OptionsValidator
**When to use:** Generate validation for options classes with source generation.

**Example use case:** Strongly-typed configuration validation.

### ValidateEnumeratedItems
**When to use:** Enable validation for items in enumerable options properties.

**Example use case:** Validating collection items in configuration.

### ValidateObjectMembers
**When to use:** Enable recursive validation for complex object properties in options.

**Example use case:** Nested configuration object validation.

---

## Summary

This reference covers all 496 attributes found in the .NET runtime, organized by namespace with practical guidance for each. Attributes range from:

- **Compiler-applied attributes** (CompilerGenerated, AsyncStateMachine, etc.) that are auto-applied by the compiler
- **Interop attributes** (DllImport, StructLayout, COM interop) for native code integration
- **Serialization attributes** (DataContract, JsonConverter, XmlElement) for controlling data formats
- **Validation attributes** (Required, Range, RegularExpression) for data validation
- **Runtime behavior attributes** (MethodImpl, ThreadStatic, ModuleInitializer) controlling execution
- **Design-time attributes** (Browsable, Category, Designer) for Visual Studio integration
- **Modern C# attributes** (RequiredMember, UnsafeAccessor, InlineArray) for latest language features
- **AOT/Trimming attributes** (RequiresUnreferencedCode, DynamicallyAccessedMembers) for native compilation

Use this guide to understand when and how to apply attributes effectively in your .NET applications.
